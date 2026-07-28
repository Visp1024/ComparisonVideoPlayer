using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Shape = System.Windows.Shapes.Shape;
using System.Windows.Threading;
using ComparisonPlayer.Cache;
using ComparisonPlayer.Playback;
using ComparisonPlayer.Tracks;

namespace ComparisonPlayer;

/// <summary>
/// Фаза 4: дисковый кэш кадров, теперь на каждый трек свой. Окно решает, откуда брать
/// кадры (прямой декод или all-intra прокси), показывает ход фоновой сборки и отдаёт
/// таймлайну миниатюры. Состояние кэша живёт в <see cref="PlayerTrack"/> — здесь только
/// решения и интерфейс.
/// </summary>
public partial class MainWindow
{
    private readonly FrameCacheStore _cacheStore = new();
    private ProxyCacheBuilder? _builder;
    private ProxyCacheBuilder Builder => _builder ??= new ProxyCacheBuilder(_cacheStore);

    /// <summary>Подпись для ключа запомненных замеров: от параметров прокси не зависит.</summary>
    private const string ProbeParameters = "step-probe-v1";

    /// <summary>
    /// Трек, кэш которого собирается прямо сейчас. Сборки идут по очереди: два ffmpeg'а
    /// на 4K делили бы диск и процессор и только мешали бы друг другу.
    /// </summary>
    private PlayerTrack? _building;

    /// <summary>Трек, дожидающийся своей очереди на сборку.</summary>
    private PlayerTrack? _queued;

    /// <summary>
    /// Минимальный задел, при котором есть смысл переходить на собираемый кэш:
    /// хвост у края собранной части всё равно упрётся в исходник.
    /// </summary>
    private const long PartialHeadStart = 90;

    /// <summary>На столько кадров кэш должен убежать вперёд, чтобы его стоило перечитывать.</summary>
    private const long PartialRefreshStep = 150;

    /// <summary>Переключатели режима расставляет код — реагировать на это не нужно.</summary>
    private bool _syncingCacheUi;

    private long CacheLimitBytes => (long)(App.Settings.CacheLimitGb * 1024 * 1024 * 1024);

    // ---------- инициализация ----------

    private void InitCacheUi()
    {
        _syncingCacheUi = true;
        var button = App.Settings.CacheMode switch
        {
            FrameCacheMode.Always => RbAlways,
            FrameCacheMode.Never => RbNever,
            _ => RbAuto
        };
        button.IsChecked = true;

        var chips = CacheFpsChips.Children.OfType<RadioButton>().ToList();
        var chip = chips.FirstOrDefault(
            x => x.Tag is string tag
                 && double.TryParse(tag, CultureInfo.InvariantCulture, out var fps)
                 && Math.Abs(fps - App.Settings.CacheFps) < 0.001) ?? chips[0];
        chip.IsChecked = true;

        _syncingCacheUi = false;

        RbAutoHint.Text = $"строить, если шаг назад медленнее {App.Settings.StepBackThresholdMs} мс";

        UpdateModeBadges();
        UpdateCachePanel();
    }

    // ---------- решение о кэше ----------

    /// <summary>
    /// Что делать с только что открытым файлом трека: играть как есть, взять готовый
    /// кэш или собрать новый. Вызывается после того, как первый кадр уже на экране.
    /// </summary>
    private void DecideCache(PlayerTrack track, string path)
    {
        if (!track.IsOpen || track.Media is not { } media) return;

        try
        {
            track.CacheKey = CacheKey.For(path, ProxyCacheBuilder.Signature(App.Settings.CacheFps));

            // Скорость шага назад — свойство исходника, а не прокси, поэтому её
            // ключ не зависит от параметров сборки: смена частоты прокси не
            // должна приводить к повторному замеру того же файла.
            track.ProbeKey = CacheKey.For(path, ProbeParameters);

            // Превью нужны в любом режиме, поэтому их ключ считается даже тогда,
            // когда прокси не будет вовсе.
            track.ThumbKey = CacheKey.For(path, ProxyCacheBuilder.ThumbnailVersion);
        }
        catch (Exception ex)
        {
            Status($"{track.Letter}: кэш недоступен, не прочитать файл ({ex.Message})");
            return;
        }

        RequestThumbnails(track);

        if (App.Settings.CacheMode == FrameCacheMode.Never)
        {
            UpdateModeBadges();
            return;
        }

        track.SourceStepMs = ProbeCache.Get(track.ProbeKey) ?? 0;

        if (_cacheStore.Find(track.CacheKey) is { } ready)
        {
            var built = ready.CreatedUtc.ToLocalTime().ToString("dd.MM HH:mm");
            UseCacheBackend(track, ready, $"{track.Letter}: кэш от {built} переиспользован — сборка не нужна");
            return;
        }

        if (App.Settings.CacheMode == FrameCacheMode.Auto)
        {
            if (StepSpeedProbe.IsAllIntra(media))
            {
                Status($"{track.Letter}: {media.Codec} — каждый кадр ключевой, кэш не нужен");
                UpdateModeBadges();
                return;
            }

            if (track.SourceStepMs <= 0) track.SourceStepMs = MeasureSourceStep(track, media);
            UpdateModeBadges();
            UpdateCachePanel();

            if (track.SourceStepMs <= App.Settings.StepBackThresholdMs)
            {
                Status($"{track.Letter}: шаг назад {track.SourceStepMs:F0} мс — кэш не нужен " +
                       $"(порог {App.Settings.StepBackThresholdMs} мс)");
                return;
            }

            Status($"{track.Letter}: шаг назад {track.SourceStepMs:F0} мс — строю кэш кадров " +
                   $"(порог {App.Settings.StepBackThresholdMs} мс)");
        }

        StartBuild(track, media);
    }

    /// <summary>
    /// Замер шага назад. Он и есть та самая секунда на long-GOP, поэтому сначала
    /// показываем сообщение и даём окну перерисоваться, а результат запоминаем —
    /// повторно этот файл мерить не придётся.
    /// </summary>
    private double MeasureSourceStep(PlayerTrack track, MediaInfo media)
    {
        Status($"{track.Letter}: замеряю скорость шага назад…");
        Dispatcher.Invoke(() => { }, DispatcherPriority.Render);

        var ms = StepSpeedProbe.Measure(track.Backend, media);
        if (track.ProbeKey is { } key) ProbeCache.Set(key, ms);
        return ms;
    }

    // ---------- сборка ----------

    private void StartBuild(PlayerTrack track, MediaInfo media)
    {
        if (track.BuildCts is not null || track.CacheKey is null) return;

        if (!File.Exists(AppEnv.FFmpegExe) && AppEnv.FFmpegExe != "ffmpeg")
        {
            Status("ffmpeg не найден — кэш собрать нечем");
            return;
        }

        // Сборки идут по очереди: параллельные ffmpeg'и на двух 4K-роликах
        // делят диск и процессор и оба работают медленнее, чем поодиночке.
        if (_building is not null && !ReferenceEquals(_building, track))
        {
            _queued = track;
            Status($"{track.Letter}: кэш соберётся после трека {_building.Letter}");
            UpdateModeBadges();
            return;
        }

        var key = track.CacheKey;
        var cts = new CancellationTokenSource();
        track.BuildCts = cts;
        track.BuildPercent = 0;
        _building = track;

        ShowBuildBar(true);
        UpdateModeBadges();
        UpdateCachePanel();

        var progress = new Progress<BuildProgress>(p => OnBuildProgress(track, p));

        Task.Run(() => Builder.BuildAsync(media, key, App.Settings.CacheFps, progress, cts.Token), cts.Token)
            .ContinueWith(task => OnBuildFinished(track, task, key, cts),
                CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void OnBuildProgress(PlayerTrack track, BuildProgress progress)
    {
        track.BuildPercent = progress.Percent;

        // Ход сборки показывает нижняя кромка клипа на таймлайне — она же говорит,
        // докуда кэш уже готов. Проценты и оставшееся время идут в индикатор режима.
        track.BuiltFraction = Math.Clamp(progress.Percent / 100.0, 0, 1);
        track.BuildEta = progress.Eta > TimeSpan.Zero ? Eta(progress.Eta) : "";

        BuildBadge(track).ToolTip =
            $"{track.Letter}: {progress.Frame} / {progress.Total} кадров" +
            (progress.Eta > TimeSpan.Zero ? $" · осталось ~{Eta(progress.Eta)}" : "") +
            (progress.Speed > 0 ? $" · {progress.Speed:F2}× реального времени" : "");

        if (progress.Stage == BuildStage.Proxy)
        {
            track.BuiltFrames = progress.Frame;
            FollowPartialCache(track);
        }

        RefreshTimeline();
        UpdateModeBadges();
    }

    /// <summary>
    /// Подхватить собираемый кэш, не дожидаясь конца сборки: как только записанная
    /// часть прокси накрывает текущую позицию, играем из неё, а по мере роста файла
    /// перечитываем его. Пока задел не набран — остаёмся на исходнике.
    /// </summary>
    private void FollowPartialCache(PlayerTrack track)
    {
        if (track.BuildCts is null || track.CacheKey is null || !track.IsOpen) return;

        var local = _sync.LocalFrame(track, _sync.PositionFrame);

        if (track.Backend is FrameCacheBackend cache)
        {
            if (!cache.Entry.Partial) return;

            // Перечитывать растущий файл просто так нельзя: открытие сбрасывает
            // позицию, и на паузе playhead прилипал бы к краю собранного. Делаем
            // это только при воспроизведении, когда до края осталось немного, —
            // на паузе кадр за краем запросит сам пользователь (см. SeekTrackFrame).
            if (!track.Backend.IsPlaying) return;
            if (local < cache.AvailableFrames - PartialRefreshStep) return;
            if (track.BuiltFrames <= cache.AvailableFrames) return;

            // Воспроизведение обгоняет сборку — дальше кадров нет, играем с исходника.
            if (ExtendPartialCache(track, cache) <= local + PartialRefreshStep / 2)
                PlayFromSource(track, $"{track.Letter}: воспроизведение обогнало сборку — играю с исходника");

            return;
        }

        if (track.BuiltFrames < PartialHeadStart) return;
        if (local >= track.BuiltFrames - PartialHeadStart / 2) return;

        var entry = PartialEntry(track);
        if (entry is null || !File.Exists(entry.ProxyPath)) return;

        UseCacheBackend(track, entry, $"{track.Letter}: играю из собираемого кэша — готово {track.BuildPercent:F0} %");
    }

    /// <summary>
    /// Перечитать растущий прокси, сохранив позицию и состояние воспроизведения:
    /// после этого доступны все кадры, дописанные с момента открытия.
    /// </summary>
    /// <returns>Сколько кадров стало доступно.</returns>
    private long ExtendPartialCache(PlayerTrack track, FrameCacheBackend cache)
    {
        var frame = track.Backend.FrameIndex;
        var playing = track.Backend.IsPlaying;

        if (!cache.Reopen().Success) return cache.AvailableFrames;

        track.Backend.SeekToFrame(frame);
        if (playing) track.Backend.Play();

        UpdateState();
        return cache.AvailableFrames;
    }

    /// <summary>
    /// Описание ещё не готовой записи: файл прокси уже растёт, а entry.json появится
    /// только в конце сборки, поэтому на диск такая запись не сохраняется.
    /// </summary>
    private CacheEntry? PartialEntry(PlayerTrack track)
    {
        if (track.CacheKey is not { } key || track.Flyleaf.Media is not { } media) return null;

        var fps = App.Settings.CacheFps > 0 ? App.Settings.CacheFps : media.Fps;
        var frames = fps > 0 && media.Duration > TimeSpan.Zero
            ? (long)Math.Round(media.Duration.TotalSeconds * fps)
            : media.FrameCount;

        return new CacheEntry
        {
            Key = key,
            Directory = _cacheStore.DirectoryFor(key),
            SourcePath = media.FilePath,
            Codec = media.Codec,
            Width = media.Width,
            Height = media.Height,
            SourceFps = media.Fps,
            Fps = fps,
            FrameCount = Math.Max(frames, 1),
            DurationTicks = media.Duration.Ticks,
            ProxyFile = ProxyCacheBuilder.ProxyFile,
            Partial = true
        };
    }

    private void OnBuildFinished(PlayerTrack track, Task<CacheEntry> task, string key, CancellationTokenSource cts)
    {
        // Пока сборка шла, файл могли закрыть и открыть другой — тогда результат чужой.
        var current = ReferenceEquals(cts, track.BuildCts);
        if (current)
        {
            track.BuildCts = null;
            if (ReferenceEquals(_building, track)) _building = null;
            ShowBuildBar(false);
        }
        cts.Dispose();

        if (task.IsCanceled || (task.IsFaulted && task.Exception?.InnerException is OperationCanceledException))
        {
            if (current)
            {
                DropPartialCache(track, key, $"{track.Letter}: сборка кэша отменена — играю с исходника");
                UpdateModeBadges();
                UpdateCachePanel();
            }
            StartQueuedBuild();
            return;
        }

        if (task.IsFaulted)
        {
            var message = task.Exception?.InnerException?.Message ?? "неизвестная ошибка";
            if (current)
            {
                DropPartialCache(track, key, $"{track.Letter}: кэш не собрался — {message}");
                UpdateModeBadges();
                UpdateCachePanel();
            }
            StartQueuedBuild();
            return;
        }

        if (current && track.CacheKey == key)
        {
            var entry = task.Result;

            // Вытеснению не отдаём ни прокси, ни превью открытых сейчас файлов.
            var freed = _cacheStore.Trim(CacheLimitBytes, key, _a.ThumbKey, _b.ThumbKey);
            var note = freed > 0 ? $", вытеснено записей: {freed}" : "";

            UseCacheBackend(track, entry, $"{track.Letter}: кэш собран ({Size(entry.Bytes)}){note}");
        }

        StartQueuedBuild();
    }

    /// <summary>Запустить сборку следующего трека, дождавшегося очереди.</summary>
    private void StartQueuedBuild()
    {
        if (_building is not null || _queued is not { } next) return;

        _queued = null;
        if (next.IsOpen && next.Media is { FromCache: false } media && next.BuildCts is null)
            StartBuild(next, media);
    }

    private void CancelBuild(PlayerTrack track)
    {
        if (ReferenceEquals(_queued, track)) _queued = null;
        track.BuildCts?.Cancel();
    }

    /// <summary>
    /// Сборка не дошла до конца: играть из обрубка нельзя, и держать его на диске
    /// незачем — целиком он всё равно не переиспользуется (entry.json не записан).
    /// </summary>
    private void DropPartialCache(PlayerTrack track, string key, string message)
    {
        track.BuiltFrames = 0;
        track.BuiltFraction = 0;

        if (track.Backend is FrameCacheBackend { Entry.Partial: true }) PlayFromSource(track, message);
        else Status(message);

        _cacheStore.Remove(key);
        RefreshTimeline();
    }

    /// <summary>
    /// Молчит ли трек оттого, что его прокси собирался до того, как кэш научился
    /// нести звук (задача #20). Такую запись не выбрасываем — кадры в ней исправны,
    /// поэтому и в ключ кэша звук не входит; но сказать, что звук вернёт пересборка,
    /// надо: иначе тишина на кэшированном ролике выглядит поломкой.
    /// </summary>
    private static bool CacheBuiltWithoutAudio(PlayerTrack track) =>
        track.Media is { FromCache: true }
        && !track.Backend.HasAudio
        && track.CacheEntry?.AudioVersion != ProxyCacheBuilder.AudioVersion;

    // ---------- переключение движка ----------

    /// <summary>Перевести трек на кадры из кэша, сохранив позицию и состояние.</summary>
    private void UseCacheBackend(PlayerTrack track, CacheEntry entry, string message)
    {
        if (!track.IsOpen || track.Media is null) return;

        var frame = track.Backend.FrameIndex;
        var wasPlaying = track.Backend.IsPlaying;

        // Прокси может идти на другой частоте, и если это мастер, то у мастер-кадра
        // меняется длительность. Держимся за момент времени, а не за номер кадра.
        var masterTime = _sync.PositionTime;

        track.Backend.Close();
        if (!ReferenceEquals(track.Backend, track.Flyleaf)) track.Backend.Dispose();

        var cache = new FrameCacheBackend(track.Flyleaf, entry);
        var res = cache.Open(entry.SourcePath);

        if (!res.Success)
        {
            // Кэш не открылся — возвращаемся на исходник, а запись убираем: она негодна.
            cache.Dispose();
            _cacheStore.Remove(entry.Key);
            track.CacheEntry = null;

            track.Backend = track.Flyleaf;
            _sync.Rebind();
            track.Flyleaf.Open(entry.SourcePath);
            track.Flyleaf.SeekToFrame(frame);

            Status($"{track.Letter}: кэш не подошёл ({res.Error}) — играю с исходника");
            UpdateState();
            RestoreMasterTime(masterTime);
            return;
        }

        track.Backend = cache;
        _sync.Rebind();

        // Незаконченную запись не отмечаем использованной: Touch сохранил бы
        // entry.json, и полусобранный прокси стал бы выглядеть готовым.
        if (!entry.Partial)
        {
            track.CacheEntry = entry;
            _cacheStore.Touch(entry);
        }

        track.Backend.SeekToFrame(frame);
        if (wasPlaying) track.Backend.Play();

        if (!entry.Partial)
        {
            // На прокси шаг назад стоит миллисекунды — замер честный и почти бесплатный.
            track.CacheStepMs = StepSpeedProbe.Measure(track.Backend, track.Media!);
            track.Backend.SeekToFrame(frame);

            track.BuiltFraction = 1;
        }

        UpdateState();
        RestoreMasterTime(masterTime);
        Status(message);
    }

    /// <summary>
    /// Вернуть playhead на прежний момент времени и пересчитать по нему оба трека.
    /// Смена движка меняет частоту и число кадров, поэтому «тот же кадр» после неё —
    /// уже другое время, а видел пользователь именно время.
    /// </summary>
    private void RestoreMasterTime(TimeSpan time)
    {
        if (!_sync.IsOpen) return;

        _sync.SetPosition(_sync.TimelineFrameAt(time));
        SeekFrame(_sync.PositionFrame);
    }

    /// <summary>Вернуть трек на прямой декод, ничего не открывая.</summary>
    private void UseDirectBackend(PlayerTrack track)
    {
        if (ReferenceEquals(track.Backend, track.Flyleaf)) return;

        track.Backend.Close();
        track.Backend.Dispose();

        track.Backend = track.Flyleaf;
        _sync.Rebind();
    }

    /// <summary>Перевести трек обратно на исходник, сохранив позицию.</summary>
    private void PlayFromSource(PlayerTrack track, string message)
    {
        if (!track.IsOpen || track.Media is not { } media) return;

        var frame = track.Backend.FrameIndex;
        var playing = track.Backend.IsPlaying;
        var source = media.FilePath;
        var masterTime = _sync.PositionTime;

        UseDirectBackend(track);

        var res = track.Flyleaf.Open(source);
        if (!res.Success)
        {
            Status($"{track.Letter}: не открылся исходник — {res.Error}");
            return;
        }

        track.Flyleaf.SeekToFrame(frame);
        if (playing) track.Flyleaf.Play();

        UpdateState();
        RestoreMasterTime(masterTime);
        Status(message);
    }

    // ---------- панель «Кэш…» ----------

    private void CacheMode_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncingCacheUi || sender is not RadioButton { Tag: string tag }) return;
        if (!Enum.TryParse<FrameCacheMode>(tag, out var mode) || mode == App.Settings.CacheMode) return;

        ApplyCacheMode(mode);
        UpdateModeBadges();
        UpdateCachePanel();
    }

    /// <summary>
    /// Сменить режим кэша и разобраться с открытыми треками. Вызывается и панелью
    /// «Кэш…», и окном настроек (фаза 5) — решение одно, реализация тоже.
    /// </summary>
    private void ApplyCacheMode(FrameCacheMode mode)
    {
        App.Settings.CacheMode = mode;
        App.Settings.Save();

        switch (mode)
        {
            case FrameCacheMode.Never:
                foreach (var track in _sync.Tracks)
                {
                    CancelBuild(track);
                    if (track.Backend is FrameCacheBackend)
                        PlayFromSource(track, $"{track.Letter}: режим «никогда» — играю с исходника");
                }

                Status("кэш выключен: только прямой декод");
                break;

            default:
                // В авто/всегда решение принимается заново — вдруг файлы уже открыты.
                var decided = false;
                foreach (var track in _sync.OpenTracks.ToList())
                {
                    if (track.Media is not { FromCache: false } || track.BuildCts is not null) continue;

                    DecideCache(track, track.Media.FilePath);
                    decided = true;
                }

                if (!decided) Status($"режим кэша: {ModeName(mode)}");
                break;
        }
    }

    private void CacheFps_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncingCacheUi || sender is not RadioButton { Tag: string tag }) return;
        if (!double.TryParse(tag, CultureInfo.InvariantCulture, out var fps)) return;
        if (Math.Abs(fps - App.Settings.CacheFps) < 0.001) return;

        ApplyCacheFps(fps);
    }

    /// <summary>
    /// Частота прокси входит в ключ кэша, поэтому её смена — это другой кэш:
    /// готовый подхватываем сразу, иначе решение принимается заново (замер уже
    /// запомнен, так что повторно файл не мерим).
    /// </summary>
    private void ApplyCacheFps(double fps)
    {
        App.Settings.CacheFps = fps;
        App.Settings.Save();
        Status($"частота прокси: {FpsName(fps)}");

        if (App.Settings.CacheMode == FrameCacheMode.Never)
        {
            UpdateCachePanel();
            return;
        }

        foreach (var track in _sync.OpenTracks.ToList())
        {
            CancelBuild(track);

            // Играющий прокси собран по старой частоте — сначала на исходник.
            var source = track.Media!.FilePath;
            if (track.Backend is FrameCacheBackend)
                PlayFromSource(track, $"{track.Letter}: частота прокси {FpsName(fps)} — пересобираю кэш");

            // Превью не пересматриваем: они сняты с исходника и от частоты прокси не зависят.
            track.CacheEntry = null;
            DecideCache(track, source);
        }
    }

    private void CacheRebuild_Click(object sender, RoutedEventArgs e)
    {
        var track = SideTrack;
        if (!track.IsOpen || track.BuildCts is not null) return;

        var source = track.Media!.FilePath;
        PlayFromSource(track, $"{track.Letter}: пересобираю кэш — пока играю с исходника");

        if (track.CacheKey is { } key)
        {
            _cacheStore.Remove(key);
            track.CacheEntry = null;
        }
        else
        {
            try { track.CacheKey = CacheKey.For(source, ProxyCacheBuilder.Signature(App.Settings.CacheFps)); }
            catch (Exception ex) { Status($"{track.Letter}: кэш недоступен — {ex.Message}"); return; }
        }

        if (track.Flyleaf.Media is { } media) StartBuild(track, media);
    }

    private void CacheUseSource_Click(object sender, RoutedEventArgs e)
    {
        var track = SideTrack;
        if (track.Backend is not FrameCacheBackend) return;

        PlayFromSource(track, $"{track.Letter}: играю с исходника — кэш остаётся на диске");
        UpdateCachePanel();
    }

    private void CacheCancel_Click(object sender, RoutedEventArgs e)
    {
        if (_building is not { } track) return;

        CancelBuild(track);
        Status($"{track.Letter}: отменяю сборку кэша…");
    }

    private void CacheOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_cacheStore.Root);
            Process.Start(new ProcessStartInfo(_cacheStore.Root) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Status($"не открылась папка кэша: {ex.Message}");
        }
    }

    private void CacheClear_Click(object sender, RoutedEventArgs e)
    {
        foreach (var track in _sync.Tracks) CancelBuild(track);

        // Записи открытых файлов не трогаем: удалять прокси из-под играющего плеера
        // нельзя, а превью пришлось бы тут же снимать заново.
        var keep = _sync.Tracks
            .SelectMany(t => new[] { t.Backend is FrameCacheBackend cache ? cache.Entry.Key : null, t.ThumbKey })
            .Where(k => k is not null)
            .Cast<string>()
            .ToArray();

        var removed = _cacheStore.Clear(keep);

        Status(removed > 0
            ? $"кэш очищен: удалено записей — {removed}" + (keep.Length == 0 ? "" : ", кроме открытых файлов")
            : "в кэше нечего удалять");

        UpdateCachePanel();
    }

    // ---------- индикаторы ----------

    /// <summary>
    /// Признаки идущей сборки: кнопка отмены рядом с индикатором режима и зелёная
    /// кромка клипа. Отдельной строки прогресса нет — она дублировала кромку.
    /// </summary>
    private void ShowBuildBar(bool visible)
    {
        BtnBuildCancel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

        if (visible) return;

        foreach (var track in _sync.Tracks)
        {
            if (track.BuildCts is not null) continue;

            track.BuildEta = "";

            // Собранный целиком кэш кромкой не отмечается: она отвечает на вопрос
            // «докуда уже быстро», а на готовом кэше быстро везде.
            if (track.BuiltFraction < 1) track.BuiltFraction = 0;
        }

        ModeBadge.ToolTip = null;
        ModeBadgeB.ToolTip = null;
    }

    private FrameworkElement BuildBadge(PlayerTrack track) => track.Id == TrackId.A ? ModeBadge : ModeBadgeB;

    private void UpdateModeBadges()
    {
        UpdateModeBadge(_a, ModeText, ModeDot, ModeBadge);
        UpdateModeBadge(_b, ModeTextB, ModeDotB, ModeBadgeB);
    }

    private void UpdateModeBadge(PlayerTrack track, TextBlock text, Shape dot, Border badge)
    {
        string label;
        string brush;

        if (!track.IsOpen)
        {
            label = track.Id == TrackId.A ? "файл не открыт" : $"{track.Letter} —";
            brush = "MutedBrush";
        }
        else if (track.BuildCts is not null && track.Media is { FromCache: true })
        {
            // Уже играем из кэша, хотя он ещё достраивается.
            label = $"{track.Letter} кэш · сборка {track.BuildPercent:F0} %{Remaining(track)}";
            brush = "OkBrush";
        }
        else if (track.BuildCts is not null)
        {
            label = $"{track.Letter} сборка кэша {track.BuildPercent:F0} %{Remaining(track)}";
            brush = "AccentBrush";
        }
        else if (ReferenceEquals(_queued, track))
        {
            label = $"{track.Letter} кэш в очереди";
            brush = "MutedBrush";
        }
        else if (track.Media is { FromCache: true })
        {
            label = track.CacheStepMs > 0 ? $"{track.Letter} кэш · шаг {track.CacheStepMs:F0} мс" : $"{track.Letter} кэш";
            brush = "OkBrush";
        }
        else
        {
            label = track.SourceStepMs > 0
                ? $"{track.Letter} прямой декод · шаг {track.SourceStepMs:F0} мс"
                : $"{track.Letter} прямой декод";
            brush = "MutedBrush";
        }

        text.Text = label;
        text.Foreground = (Brush)FindResource(brush);
        dot.Fill = (Brush)FindResource(brush == "MutedBrush" ? "DimBrush" : brush);
        badge.BorderBrush = (Brush)FindResource(brush == "MutedBrush" ? "LineBrush" : brush);
    }

    /// <summary>
    /// Прокси с пониженной частотой — это другой набор кадров: их номера и общее
    /// число уже не совпадают с исходником. Молчать об этом нельзя, поэтому в
    /// сведениях появляется предупреждение.
    /// </summary>
    private void UpdateProxyNote(PlayerTrack track)
    {
        var entry = track.CacheEntry;
        var reduced = track.Media is { FromCache: true }
                      && entry is not null
                      && entry.SourceFps > 0
                      && Math.Abs(entry.Fps - entry.SourceFps) > 0.01;

        InfoProxyNote.Visibility = reduced ? Visibility.Visible : Visibility.Collapsed;
        if (!reduced) return;

        InfoProxyNote.Text =
            $"Кадры идут из прокси на {entry!.Fps:0.###} fps вместо {entry.SourceFps:0.###} fps исходника: " +
            "номера кадров и их общее число относятся к прокси. Для покадрового соответствия " +
            "выберите в «Кэш…» частоту «как в исходнике».";
    }

    private void UpdateCachePanel()
    {
        var track = SideTrack;
        var open = track.IsOpen;
        var fromCache = track.Media is { FromCache: true };

        CacheFileHeader.Text = $"Т Р Е К   {track.Letter}";

        CacheFileMode.Text = !open ? "—"
            : track.BuildCts is not null && fromCache ? "кэш, идёт сборка"
            : track.BuildCts is not null ? "сборка"
            : ReferenceEquals(_queued, track) ? "в очереди на сборку"
            : fromCache ? "кэш"
            : "прямой декод";

        CacheFileStep.Text = !open ? "—"
            : $"{(track.CacheStepMs > 0 ? $"{track.CacheStepMs:F0} мс" : "—")} / " +
              $"{(track.SourceStepMs > 0 ? $"{track.SourceStepMs:F0} мс" : "—")}";

        var entry = track.CacheEntry;
        CacheFileProxy.Text = entry is null ? "—" : $"{Size(entry.Bytes)} · {entry.Fps:0.###} fps";
        CacheFileBuilt.Text = entry is null ? "—" : entry.CreatedUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm");

        CacheFileAudio.Text = !open || !fromCache ? "—"
            : track.Backend.HasAudio ? "есть"
            : CacheBuiltWithoutAudio(track) ? "нет — соберите заново"
            : "нет дорожки в исходнике";

        BtnRebuild.IsEnabled = open && track.BuildCts is null && App.Settings.CacheMode != FrameCacheMode.Never;
        BtnUseSource.IsEnabled = fromCache;

        var entries = _cacheStore.All();
        var used = entries.Sum(x => x.Bytes);
        var limit = CacheLimitBytes;

        CacheStorageText.Text = $"{Size(used)} / {App.Settings.CacheLimitGb:0.#} ГБ · записей: {entries.Count}";
        CacheStorageBar.Value = limit > 0 ? Math.Clamp(used * 100.0 / limit, 0, 100) : 0;
    }

    // ---------- превью кадров ----------

    /// <summary>
    /// Позвать превью на все открытые треки. Точка ленивого запуска: пока таймлайн
    /// свёрнут, показывать кадры негде, поэтому и снимать их незачем — вызывается
    /// при разворачивании таймлайна и после открытия файла.
    /// </summary>
    private void RefreshThumbnails()
    {
        foreach (var track in _sync.OpenTracks.ToList()) RequestThumbnails(track);
    }

    /// <summary>
    /// Показать полоску превью трека: сначала ищем готовую запись на диске, иначе
    /// снимаем кадры одним проходом ffmpeg. Превью не зависят от кэша кадров и
    /// снимаются в любом режиме — в прямом декоде клипу больше нечего показать,
    /// а весят они мегабайты против гигабайтов прокси.
    /// </summary>
    private void RequestThumbnails(PlayerTrack track)
    {
        // Свёрнутый таймлайн кадров не показывает — ffmpeg в это время не нужен.
        // Развернут он будет через ApplyCompact, который сюда и вернётся.
        if (_compact) return;

        if (track.ThumbKey is not { } key || track.ThumbCts is not null) return;
        if (track.Media is not { } media || media.Duration <= TimeSpan.Zero) return;
        if (track.ThumbFiles.Count > 0) return;

        if (_cacheStore.FindThumbnails(key) is { } ready)
        {
            _cacheStore.Touch(ready);
            ShowThumbnails(track, ready.ThumbnailFiles(), ready.ThumbnailIntervalSeconds, 1);
            return;
        }

        if (!File.Exists(AppEnv.FFmpegExe) && AppEnv.FFmpegExe != "ffmpeg") return;

        var (planned, interval) = ProxyCacheBuilder.ThumbnailPlan(media.Duration, media.FrameCount);
        if (planned == 0) return;

        var cts = new CancellationTokenSource();
        track.ThumbCts = cts;

        // Пустой список — тоже состояние: клип штрихуется и заполняется по мере съёмки.
        ShowThumbnails(track, [], interval, 0);

        var progress = new Progress<BuildProgress>(_ => FollowThumbnails(track, key, interval, planned));

        Task.Run(() => Builder.BuildThumbnailsOnlyAsync(media, key, progress, cts.Token), cts.Token)
            .ContinueWith(task => OnThumbnailsFinished(track, task, key),
                CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>
    /// Показать кадры, снятые к этому моменту. Последний файл может быть ещё недописан
    /// ffmpeg'ом, поэтому его пропускаем — иначе клетка мигала бы обрезанной картинкой.
    /// </summary>
    private void FollowThumbnails(PlayerTrack track, string key, double interval, int planned)
    {
        if (track.ThumbCts is null || track.ThumbKey != key) return;

        var dir = Path.Combine(_cacheStore.DirectoryFor(key), "thumbs");
        if (!Directory.Exists(dir)) return;

        var files = Directory.GetFiles(dir, "*.jpg");
        if (files.Length < 2) return;

        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
        var taken = files[..^1];

        ShowThumbnails(track, taken, interval, planned > 0 ? taken.Length / (double)planned : 0);
    }

    private void OnThumbnailsFinished(PlayerTrack track, Task<CacheEntry> task, string key)
    {
        // Пока снимали, файл могли закрыть или сменить — тогда результат уже чужой.
        var mine = track.ThumbKey == key;
        track.ThumbCts = null;

        if (task.IsCanceled || task.IsFaulted)
        {
            // Недоснятую полоску на диске не держим: без entry.json она всё равно
            // не найдётся, зато папка с кадрами осталась бы вне учёта объёма.
            _cacheStore.Remove(key);
            if (!mine) return;

            ClearThumbnails(track);

            if (!task.IsCanceled && task.Exception?.InnerException is not OperationCanceledException)
            {
                Status($"{track.Letter}: превью кадров не сняты — " +
                       $"{task.Exception?.InnerException?.Message ?? "неизвестная ошибка"}");
                return;
            }

            // Ключ тот же, а съёмку прервали — значит, тот же файл открыли заново,
            // пока она шла. Полоска ему всё ещё нужна, снимаем заново.
            RequestThumbnails(track);
            return;
        }

        if (!mine) return;

        var entry = task.Result;
        ShowThumbnails(track, entry.ThumbnailFiles(), entry.ThumbnailIntervalSeconds, 1);
        UpdateCachePanel();
    }

    /// <summary>Прекратить съёмку превью: файл закрывают или меняют, снятое уже не нужно.</summary>
    private void CancelThumbnails(PlayerTrack track) => track.ThumbCts?.Cancel();

    /// <summary>
    /// Отдать таймлайну снятые кадры трека вместе с их шагом по времени и долей клипа,
    /// которую они покрывают: дальше этой доли клип штрихуется.
    /// </summary>
    private void ShowThumbnails(PlayerTrack track, IReadOnlyList<string> files, double interval, double fraction)
    {
        track.ThumbFiles = files;
        track.ThumbInterval = interval;
        track.ThumbFraction = Math.Clamp(fraction, 0, 1);
        track.HasThumbnails = files.Count > 0 || track.ThumbCts is not null;

        RefreshTimeline();
    }

    /// <summary>
    /// Миниатюра для момента ролика: клетка клипа спрашивает кадр своего места на шкале.
    /// null — кадр ещё не снят (съёмка досюда не дошла), и клетка остаётся заштрихованной.
    /// </summary>
    private ImageSource? ThumbnailAt(PlayerTrack track, TimeSpan time)
    {
        if (track.ThumbFiles.Count == 0 || track.ThumbInterval <= 0) return null;

        var index = (int)(time.TotalSeconds / track.ThumbInterval);
        if (index < 0 || index >= track.ThumbFiles.Count) return null;

        return LoadThumbnail(track, track.ThumbFiles[index]);
    }

    private void ClearThumbnails(PlayerTrack track)
    {
        track.ThumbFiles = [];
        track.ThumbInterval = 0;
        track.ThumbFraction = 0;
        track.ThumbImages.Clear();
        track.HasThumbnails = false;

        RefreshTimeline();
    }

    /// <summary>
    /// Загрузка с <see cref="BitmapCacheOption.OnLoad"/>: файл сразу закрывается,
    /// иначе очистка кэша спотыкалась бы о занятые картинки.
    /// </summary>
    private static ImageSource? LoadThumbnail(PlayerTrack track, string path)
    {
        if (track.ThumbImages.TryGetValue(path, out var cached)) return cached;

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bitmap.UriSource = new Uri(path);
            bitmap.EndInit();
            bitmap.Freeze();

            track.ThumbImages[path] = bitmap;
            return bitmap;
        }
        catch (Exception)
        {
            // Файл ещё дописывается ffmpeg'ом — покажем его на следующем обновлении.
            return null;
        }
    }

    // ---------- форматирование ----------

    private static string Remaining(PlayerTrack track) => track.BuildEta.Length > 0 ? $" · ~{track.BuildEta}" : "";

    private static string FpsName(double fps) =>
        fps > 0 ? $"{fps.ToString("0.###", CultureInfo.InvariantCulture)} кадров/с" : "как в исходнике";

    private static string ModeName(FrameCacheMode mode) => mode switch
    {
        FrameCacheMode.Always => "всегда",
        FrameCacheMode.Never => "никогда",
        _ => "автоматически"
    };

    private static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.#} ГБ",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0} МБ",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):0} КБ",
        _ => $"{bytes} Б"
    };

    private static string Eta(TimeSpan eta) =>
        eta.TotalHours >= 1 ? eta.ToString(@"h\:mm\:ss") : eta.ToString(@"m\:ss");
}
