using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ComparisonPlayer.Cache;
using ComparisonPlayer.Playback;

namespace ComparisonPlayer;

/// <summary>
/// Фаза 4: дисковый кэш кадров. Окно решает, откуда брать кадры (прямой декод или
/// all-intra прокси), показывает прогресс фоновой сборки и полоску миниатюр.
/// Логика вынесена в отдельный файл частичного класса, чтобы MainWindow остался
/// про интерфейс плеера, а не про кэш.
/// </summary>
public partial class MainWindow
{
    private readonly FrameCacheStore _cacheStore = new();
    private ProxyCacheBuilder? _builder;
    private ProxyCacheBuilder Builder => _builder ??= new ProxyCacheBuilder(_cacheStore);

    /// <summary>Идущая сборка; null — сборки нет.</summary>
    private CancellationTokenSource? _buildCts;

    /// <summary>Подпись для ключа запомненных замеров: от параметров прокси не зависит.</summary>
    private const string ProbeParameters = "step-probe-v1";

    /// <summary>Ключ кэша открытого файла.</summary>
    private string? _cacheKey;

    /// <summary>Ключ замера шага назад открытого файла.</summary>
    private string? _probeKey;

    /// <summary>Готовая запись кэша открытого файла, если она есть.</summary>
    private CacheEntry? _cacheEntry;

    /// <summary>Замер шага назад на исходнике, мс; 0 — не мерили.</summary>
    private double _sourceStepMs;

    /// <summary>Замер шага назад на прокси, мс; 0 — не мерили.</summary>
    private double _cacheStepMs;

    private double _buildPercent;

    /// <summary>Оценка оставшегося времени сборки для индикатора; пусто — оценки нет.</summary>
    private string _buildEta = "";

    /// <summary>Какая доля ролика уже в кэше: по ней рисуются и полоса готовности, и лента кадров.</summary>
    private double _builtFraction;

    /// <summary>Сколько кадров прокси уже записано ffmpeg'ом; 0 — сборки нет.</summary>
    private long _builtFrames;

    /// <summary>
    /// Минимальный задел, при котором есть смысл переходить на собираемый кэш:
    /// хвост у края собранной части всё равно упрётся в исходник.
    /// </summary>
    private const long PartialHeadStart = 90;

    /// <summary>На столько кадров кэш должен убежать вперёд, чтобы его стоило перечитывать.</summary>
    private const long PartialRefreshStep = 150;

    /// <summary>Переключатели режима расставляет код — реагировать на это не нужно.</summary>
    private bool _syncingCacheUi;

    /// <summary>Playhead ведут по полоске миниатюр.</summary>
    private bool _thumbDragging;

    /// <summary>Все собранные миниатюры файла; в полоску попадает выборка по ширине.</summary>
    private IReadOnlyList<string> _thumbFiles = [];

    /// <summary>Сколько клеток сейчас в полоске и из скольких файлов они выбраны.</summary>
    private int _thumbShown;
    private int _thumbSource;

    /// <summary>Раскладку пересчитываем при каждом изменении ширины — картинки декодируем один раз.</summary>
    private readonly Dictionary<string, ImageSource> _thumbCache = [];

    private long CacheLimitBytes => (long)(App.Settings.CacheLimitGb * 1024 * 1024 * 1024);

    // ---------- инициализация и сброс ----------

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

        UpdateModeBadge();
        UpdateCachePanel();
    }

    /// <summary>Забыть всё, что относилось к прошлому файлу.</summary>
    private void ResetCacheState()
    {
        _cacheKey = null;
        _probeKey = null;
        _cacheEntry = null;
        _sourceStepMs = 0;
        _cacheStepMs = 0;
        _buildPercent = 0;
        _builtFrames = 0;
        _builtFraction = 0;

        ClearThumbnails();
        ShowBuildBar(false);
        UpdateModeBadge();
        UpdateCachePanel();
    }

    // ---------- решение о кэше ----------

    /// <summary>
    /// Что делать с только что открытым файлом: играть как есть, взять готовый кэш
    /// или собрать новый. Вызывается после того, как первый кадр уже на экране.
    /// </summary>
    private void DecideCache(string path)
    {
        if (!_backend.IsOpen || _backend.Media is not { } media) return;

        if (App.Settings.CacheMode == FrameCacheMode.Never)
        {
            UpdateModeBadge();
            return;
        }

        try
        {
            _cacheKey = CacheKey.For(path, ProxyCacheBuilder.Signature(App.Settings.CacheFps));

            // Скорость шага назад — свойство исходника, а не прокси, поэтому её
            // ключ не зависит от параметров сборки: смена частоты прокси не
            // должна приводить к повторному замеру того же файла.
            _probeKey = CacheKey.For(path, ProbeParameters);
        }
        catch (Exception ex)
        {
            Status($"кэш недоступен: не прочитать файл ({ex.Message})");
            return;
        }

        _sourceStepMs = ProbeCache.Get(_probeKey) ?? 0;

        if (_cacheStore.Find(_cacheKey) is { } ready)
        {
            var built = ready.CreatedUtc.ToLocalTime().ToString("dd.MM HH:mm");
            UseCacheBackend(ready, $"кэш от {built} переиспользован — сборка не нужна");
            return;
        }

        if (App.Settings.CacheMode == FrameCacheMode.Auto)
        {
            if (StepSpeedProbe.IsAllIntra(media))
            {
                Status($"{media.Codec}: каждый кадр ключевой, кэш не нужен");
                UpdateModeBadge();
                return;
            }

            if (_sourceStepMs <= 0) _sourceStepMs = MeasureSourceStep(media, _probeKey!);
            UpdateModeBadge();
            UpdateCachePanel();

            if (_sourceStepMs <= App.Settings.StepBackThresholdMs)
            {
                Status($"шаг назад {_sourceStepMs:F0} мс — кэш не нужен (порог {App.Settings.StepBackThresholdMs} мс)");
                return;
            }

            Status($"шаг назад {_sourceStepMs:F0} мс — строю кэш кадров (порог {App.Settings.StepBackThresholdMs} мс)");
        }

        StartBuild(media);
    }

    /// <summary>
    /// Замер шага назад. Он и есть та самая секунда на long-GOP, поэтому сначала
    /// показываем сообщение и даём окну перерисоваться, а результат запоминаем —
    /// повторно этот файл мерить не придётся.
    /// </summary>
    private double MeasureSourceStep(MediaInfo media, string key)
    {
        Status("замеряю скорость шага назад…");
        Dispatcher.Invoke(() => { }, DispatcherPriority.Render);

        var ms = StepSpeedProbe.Measure(_backend, media);
        ProbeCache.Set(key, ms);
        return ms;
    }

    // ---------- сборка ----------

    private void StartBuild(MediaInfo media)
    {
        if (_buildCts is not null || _cacheKey is null) return;

        if (!File.Exists(AppEnv.FFmpegExe) && AppEnv.FFmpegExe != "ffmpeg")
        {
            Status("ffmpeg не найден — кэш собрать нечем");
            return;
        }

        var key = _cacheKey;
        var cts = new CancellationTokenSource();
        _buildCts = cts;
        _buildPercent = 0;

        ShowBuildBar(true);
        ShowThumbnails([]);   // полоска появляется сразу и заполняется по мере сборки
        UpdateModeBadge();
        UpdateCachePanel();

        var progress = new Progress<BuildProgress>(OnBuildProgress);

        Task.Run(() => Builder.BuildAsync(media, key, App.Settings.CacheFps, withThumbnails: true, progress, cts.Token), cts.Token)
            .ContinueWith(task => OnBuildFinished(task, key, cts),
                CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void OnBuildProgress(BuildProgress progress)
    {
        _buildPercent = progress.Percent;

        // Ход сборки показывает одна полоса под шкалой — она же говорит, докуда
        // кэш уже готов. Проценты и оставшееся время идут в индикатор режима.
        _builtFraction = Math.Clamp(progress.Percent / 100.0, 0, 1);
        BuiltLine.Visibility = Visibility.Visible;
        BuiltFill.Width = BuiltLine.ActualWidth * _builtFraction;

        _buildEta = progress.Eta > TimeSpan.Zero ? Eta(progress.Eta) : "";

        ModeBadge.ToolTip =
            $"{progress.Frame} / {progress.Total} кадров" +
            (progress.Eta > TimeSpan.Zero ? $" · осталось ~{Eta(progress.Eta)}" : "") +
            (progress.Speed > 0 ? $" · {progress.Speed:F2}× реального времени" : "");

        // Миниатюры строятся параллельно с прокси и показываются по мере появления;
        // последний файл может быть ещё недописан, поэтому его пропускаем.
        if (_cacheKey is { } key)
        {
            var dir = Path.Combine(_cacheStore.DirectoryFor(key), "thumbs");
            if (Directory.Exists(dir))
            {
                var files = Directory.GetFiles(dir, "*.jpg");
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                if (files.Length > 1) ShowThumbnails(files[..^1]);
            }
        }

        if (progress.Stage == BuildStage.Proxy)
        {
            _builtFrames = progress.Frame;
            FollowPartialCache();
        }

        UpdateModeBadge();
    }

    /// <summary>
    /// Подхватить собираемый кэш, не дожидаясь конца сборки: как только записанная
    /// часть прокси накрывает текущую позицию, играем из неё, а по мере роста файла
    /// перечитываем его. Пока задел не набран — остаёмся на исходнике.
    /// </summary>
    private void FollowPartialCache()
    {
        if (_buildCts is null || _cacheKey is null || !_backend.IsOpen) return;

        if (_backend is FrameCacheBackend cache)
        {
            if (!cache.Entry.Partial) return;

            // Перечитывать растущий файл просто так нельзя: открытие сбрасывает
            // позицию, и на паузе playhead прилипал бы к краю собранного. Делаем
            // это только при воспроизведении, когда до края осталось немного, —
            // на паузе кадр за краем запросит сам пользователь (см. SeekFrame).
            if (!_backend.IsPlaying) return;
            if (_backend.FrameIndex < cache.AvailableFrames - PartialRefreshStep) return;
            if (_builtFrames <= cache.AvailableFrames) return;

            // Воспроизведение обгоняет сборку — дальше кадров нет, играем с исходника.
            if (ExtendPartialCache(cache) <= _backend.FrameIndex + PartialRefreshStep / 2)
                PlayFromSource("воспроизведение обогнало сборку кэша — играю с исходника");

            return;
        }

        if (_builtFrames < PartialHeadStart) return;
        if (_backend.FrameIndex >= _builtFrames - PartialHeadStart / 2) return;

        var entry = PartialEntry();
        if (entry is null || !File.Exists(entry.ProxyPath)) return;

        UseCacheBackend(entry, $"играю из собираемого кэша — готово {_buildPercent:F0} %");
    }

    /// <summary>
    /// Перечитать растущий прокси, сохранив позицию и состояние воспроизведения:
    /// после этого доступны все кадры, дописанные с момента открытия.
    /// </summary>
    /// <returns>Сколько кадров стало доступно.</returns>
    private long ExtendPartialCache(FrameCacheBackend cache)
    {
        var frame = _backend.FrameIndex;
        var playing = _backend.IsPlaying;

        if (!cache.Reopen().Success) return cache.AvailableFrames;

        _backend.SeekToFrame(frame);
        if (playing) _backend.Play();

        UpdateState();
        return cache.AvailableFrames;
    }

    /// <summary>
    /// Описание ещё не готовой записи: файл прокси уже растёт, а entry.json появится
    /// только в конце сборки, поэтому на диск такая запись не сохраняется.
    /// </summary>
    private CacheEntry? PartialEntry()
    {
        if (_cacheKey is not { } key || _flyleaf.Media is not { } media) return null;

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

    private void OnBuildFinished(Task<CacheEntry> task, string key, CancellationTokenSource cts)
    {
        // Пока сборка шла, файл могли закрыть и открыть другой — тогда результат чужой.
        var current = ReferenceEquals(cts, _buildCts);
        if (current)
        {
            _buildCts = null;
            ShowBuildBar(false);
        }
        cts.Dispose();

        if (task.IsCanceled || (task.IsFaulted && task.Exception?.InnerException is OperationCanceledException))
        {
            if (current)
            {
                DropPartialCache(key, "сборка кэша отменена — играю с исходника");
                UpdateModeBadge();
                UpdateCachePanel();
            }
            return;
        }

        if (task.IsFaulted)
        {
            var message = task.Exception?.InnerException?.Message ?? "неизвестная ошибка";
            if (current)
            {
                DropPartialCache(key, $"кэш не собрался: {message}");
                UpdateModeBadge();
                UpdateCachePanel();
            }
            return;
        }

        if (!current || _cacheKey != key) return;

        var entry = task.Result;
        var freed = _cacheStore.Trim(CacheLimitBytes, key);
        var note = freed > 0 ? $", вытеснено записей: {freed}" : "";

        UseCacheBackend(entry, $"кэш собран ({Size(entry.Bytes)}){note}");
    }

    private void CancelBuild() => _buildCts?.Cancel();

    /// <summary>
    /// Сборка не дошла до конца: играть из обрубка нельзя, и держать его на диске
    /// незачем — целиком он всё равно не переиспользуется (entry.json не записан).
    /// </summary>
    private void DropPartialCache(string key, string message)
    {
        _builtFrames = 0;

        if (_backend is FrameCacheBackend { Entry.Partial: true }) PlayFromSource(message);
        else Status(message);

        _cacheStore.Remove(key);
    }

    // ---------- переключение движка ----------

    /// <summary>Перевести плеер на кадры из кэша, сохранив позицию и состояние.</summary>
    private void UseCacheBackend(CacheEntry entry, string message)
    {
        if (!_backend.IsOpen || _backend.Media is null) return;

        var frame = _backend.FrameIndex;
        var wasPlaying = _backend.IsPlaying;

        Unsubscribe(_backend);
        _backend.Close();
        if (!ReferenceEquals(_backend, _flyleaf)) _backend.Dispose();

        var cache = new FrameCacheBackend(_flyleaf, entry);
        var res = cache.Open(entry.SourcePath);

        if (!res.Success)
        {
            // Кэш не открылся — возвращаемся на исходник, а запись убираем: она негодна.
            cache.Dispose();
            _cacheStore.Remove(entry.Key);
            _cacheEntry = null;

            _backend = _flyleaf;
            Subscribe(_backend);
            _flyleaf.Open(entry.SourcePath);
            _flyleaf.SeekToFrame(frame);

            Status($"кэш не подошёл ({res.Error}) — играю с исходника");
            UpdateModeBadge();
            UpdateCachePanel();
            return;
        }

        _backend = cache;
        Subscribe(_backend);

        // Незаконченную запись не отмечаем использованной: Touch сохранил бы
        // entry.json, и полусобранный прокси стал бы выглядеть готовым.
        if (!entry.Partial)
        {
            _cacheEntry = entry;
            _cacheStore.Touch(entry);
        }

        _backend.SeekToFrame(frame);
        if (wasPlaying) _backend.Play();

        if (!entry.Partial)
        {
            // На прокси шаг назад стоит миллисекунды — замер честный и почти бесплатный.
            _cacheStepMs = StepSpeedProbe.Measure(_backend, _backend.Media!);
            _backend.SeekToFrame(frame);
        }

        if (!entry.Partial)
        {
            _builtFraction = 1;
            ShowThumbnails(entry.ThumbnailFiles());
        }
        UpdateState();
        UpdateModeBadge();
        UpdateCachePanel();
        Status(message);
    }

    /// <summary>Вернуть окно на прямой декод, ничего не открывая.</summary>
    private void UseDirectBackend()
    {
        if (ReferenceEquals(_backend, _flyleaf)) return;

        Unsubscribe(_backend);
        _backend.Close();
        _backend.Dispose();

        _backend = _flyleaf;
        Subscribe(_backend);
    }

    // ---------- панель «Кэш…» ----------

    private void Cache_Click(object sender, RoutedEventArgs e) => ToggleCachePanel();

    /// <summary>
    /// Панель кэша и панель сведений — обе накладки у правого края, поэтому
    /// открытая всегда одна: вторая сворачивается.
    /// </summary>
    private void ToggleCachePanel()
    {
        var open = CachePanel.Visibility == Visibility.Visible;

        if (!open)
        {
            UpdateCachePanel();
            InfoPanel.Visibility = Visibility.Collapsed;
        }

        CachePanel.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
        BtnCache.Foreground = (Brush)FindResource(open ? "TextBrush" : "AccentBrush");
        BtnCache.BorderBrush = (Brush)FindResource(open ? "LineBrush" : "AccentDim");
        Status(open ? "панель кэша свёрнута (C)" : "панель кэша открыта (C)");
    }

    private void CacheMode_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncingCacheUi || sender is not RadioButton { Tag: string tag }) return;
        if (!Enum.TryParse<FrameCacheMode>(tag, out var mode) || mode == App.Settings.CacheMode) return;

        App.Settings.CacheMode = mode;
        App.Settings.Save();

        switch (mode)
        {
            case FrameCacheMode.Never:
                CancelBuild();
                if (_backend is FrameCacheBackend) PlayFromSource("режим «никогда» — играю с исходника");
                else Status("кэш выключен: только прямой декод");
                break;

            default:
                // В авто/всегда решение принимается заново — вдруг файл уже открыт.
                if (_backend.IsOpen && _backend.Media is { FromCache: false } && _buildCts is null)
                    DecideCache(_backend.Media.FilePath);
                else
                    Status($"режим кэша: {ModeName(mode)}");
                break;
        }

        UpdateModeBadge();
        UpdateCachePanel();
    }

    /// <summary>
    /// Частота прокси входит в ключ кэша, поэтому её смена — это другой кэш:
    /// готовый подхватываем сразу, иначе решение принимается заново (замер уже
    /// запомнен, так что повторно файл не мерим).
    /// </summary>
    private void CacheFps_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncingCacheUi || sender is not RadioButton { Tag: string tag }) return;
        if (!double.TryParse(tag, CultureInfo.InvariantCulture, out var fps)) return;
        if (Math.Abs(fps - App.Settings.CacheFps) < 0.001) return;

        App.Settings.CacheFps = fps;
        App.Settings.Save();
        Status($"частота прокси: {FpsName(fps)}");

        if (!_backend.IsOpen || App.Settings.CacheMode == FrameCacheMode.Never)
        {
            UpdateCachePanel();
            return;
        }

        CancelBuild();

        // Играющий прокси собран по старой частоте — сначала возвращаемся на исходник.
        var source = _backend.Media!.FilePath;
        if (_backend is FrameCacheBackend) PlayFromSource($"частота прокси: {FpsName(fps)} — пересобираю кэш");

        _cacheEntry = null;
        ClearThumbnails();
        DecideCache(source);
    }

    private void CacheRebuild_Click(object sender, RoutedEventArgs e)
    {
        if (!_backend.IsOpen || _buildCts is not null) return;

        var source = _backend.Media!.FilePath;
        PlayFromSource("пересобираю кэш — пока играю с исходника");

        if (_cacheKey is { } key)
        {
            _cacheStore.Remove(key);
            _cacheEntry = null;
        }
        else
        {
            try { _cacheKey = CacheKey.For(source, ProxyCacheBuilder.Signature(App.Settings.CacheFps)); }
            catch (Exception ex) { Status($"кэш недоступен: {ex.Message}"); return; }
        }

        ClearThumbnails();
        if (_flyleaf.Media is { } media) StartBuild(media);
    }

    private void CacheUseSource_Click(object sender, RoutedEventArgs e)
    {
        if (_backend is not FrameCacheBackend) return;
        PlayFromSource("играю с исходника — кэш остаётся на диске");
        UpdateCachePanel();
    }

    private void CacheCancel_Click(object sender, RoutedEventArgs e)
    {
        if (_buildCts is null) return;
        CancelBuild();
        Status("отменяю сборку кэша…");
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
        CancelBuild();

        // Запись открытого файла не трогаем: удалять прокси из-под играющего плеера нельзя.
        var keep = _backend is FrameCacheBackend cache ? cache.Entry.Key : null;
        var removed = _cacheStore.Clear(keep);

        Status(removed > 0
            ? $"кэш очищен: удалено записей — {removed}" + (keep is null ? "" : ", кроме открытого файла")
            : "в кэше нечего удалять");

        UpdateCachePanel();
    }

    /// <summary>Перевести плеер обратно на исходник, сохранив позицию.</summary>
    private void PlayFromSource(string message)
    {
        if (!_backend.IsOpen || _backend.Media is not { } media) return;

        var frame = _backend.FrameIndex;
        var playing = _backend.IsPlaying;
        var source = media.FilePath;

        UseDirectBackend();

        var res = _flyleaf.Open(source);
        if (!res.Success)
        {
            Status($"не открылся исходник: {res.Error}");
            return;
        }

        _flyleaf.SeekToFrame(frame);
        if (playing) _flyleaf.Play();
        UpdateState();
        UpdateModeBadge();
        Status(message);
    }

    // ---------- индикаторы ----------

    /// <summary>
    /// Признаки идущей сборки: кнопка отмены рядом с индикатором режима и полоса
    /// готовности под шкалой. Отдельной строки прогресса нет — она дублировала полосу.
    /// </summary>
    private void ShowBuildBar(bool visible)
    {
        BtnBuildCancel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

        if (visible) return;

        _buildEta = "";
        ModeBadge.ToolTip = null;
        BuiltLine.Visibility = Visibility.Collapsed;
        BuiltFill.Width = 0;
    }

    private void UpdateModeBadge()
    {
        string text;
        string brush;

        if (!_backend.IsOpen)
        {
            text = "файл не открыт";
            brush = "MutedBrush";
        }
        else if (_buildCts is not null && _backend.Media is { FromCache: true })
        {
            // Уже играем из кэша, хотя он ещё достраивается.
            text = $"кэш · сборка {_buildPercent:F0} %{Remaining()}";
            brush = "OkBrush";
        }
        else if (_buildCts is not null)
        {
            text = $"сборка кэша {_buildPercent:F0} %{Remaining()}";
            brush = "AccentBrush";
        }
        else if (_backend.Media is { FromCache: true })
        {
            text = _cacheStepMs > 0 ? $"кэш · шаг {_cacheStepMs:F0} мс" : "кэш";
            brush = "OkBrush";
        }
        else
        {
            text = _sourceStepMs > 0 ? $"прямой декод · шаг {_sourceStepMs:F0} мс" : "прямой декод";
            brush = "MutedBrush";
        }

        ModeText.Text = text;
        ModeText.Foreground = (Brush)FindResource(brush);
        ModeDot.Fill = (Brush)FindResource(brush == "MutedBrush" ? "DimBrush" : brush);
        ModeBadge.BorderBrush = (Brush)FindResource(brush == "MutedBrush" ? "LineBrush" : brush);
    }

    /// <summary>
    /// Прокси с пониженной частотой — это другой набор кадров: их номера и общее
    /// число уже не совпадают с исходником. Молчать об этом нельзя, поэтому в
    /// сведениях появляется предупреждение.
    /// </summary>
    private void UpdateProxyNote()
    {
        var entry = _cacheEntry;
        var reduced = _backend.Media is { FromCache: true }
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
        var open = _backend.IsOpen;
        var fromCache = _backend.Media is { FromCache: true };

        CacheFileMode.Text = !open ? "—"
            : _buildCts is not null && fromCache ? "кэш, идёт сборка"
            : _buildCts is not null ? "сборка"
            : fromCache ? "кэш"
            : "прямой декод";

        CacheFileStep.Text = !open ? "—"
            : $"{(_cacheStepMs > 0 ? $"{_cacheStepMs:F0} мс" : "—")} / " +
              $"{(_sourceStepMs > 0 ? $"{_sourceStepMs:F0} мс" : "—")}";

        var entry = _cacheEntry;
        CacheFileProxy.Text = entry is null
            ? "—"
            : $"{Size(entry.Bytes)} · {entry.Fps:0.###} fps";
        CacheFileBuilt.Text = entry is null ? "—" : entry.CreatedUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm");

        BtnRebuild.IsEnabled = open && _buildCts is null && App.Settings.CacheMode != FrameCacheMode.Never;
        BtnUseSource.IsEnabled = fromCache;

        var entries = _cacheStore.All();
        var used = entries.Sum(x => x.Bytes);
        var limit = CacheLimitBytes;

        CacheStorageText.Text = $"{Size(used)} / {App.Settings.CacheLimitGb:0.#} ГБ · записей: {entries.Count}";
        CacheStorageBar.Value = limit > 0 ? Math.Clamp(used * 100.0 / limit, 0, 100) : 0;
    }

    // ---------- миниатюры ----------

    /// <summary>
    /// Показать полоску с теми миниатюрами, что уже сняты. Пустой список — это тоже
    /// полоска: во время сборки она видна сразу и заполняется слева направо.
    /// </summary>
    private void ShowThumbnails(IReadOnlyList<string> files)
    {
        _thumbFiles = files;
        LayoutThumbnails();
    }

    /// <summary>
    /// Раскладка полоски: клетки покрывают весь ролик, и каждая показывает кадр
    /// своего места на шкале времени. Пока кэш собирается, кадры есть только слева —
    /// правые клетки остаются пустыми и заполняются по мере сборки.
    /// </summary>
    private void LayoutThumbnails()
    {
        if (_backend.Media is not { } media || media.Duration <= TimeSpan.Zero || _thumbFiles.Count == 0)
        {
            ThumbStrip.Children.Clear();
            ThumbStripBox.Width = 0;
            _thumbShown = 0;
            _thumbSource = 0;

            // Во время сборки место под полоску уже занято: она вот-вот появится.
            ThumbStripArea.Visibility = _buildCts is not null ? Visibility.Visible : Visibility.Collapsed;
            return;
        }

        ThumbStripArea.Visibility = Visibility.Visible;

        var (planned, interval) = ProxyCacheBuilder.ThumbnailPlan(media.Duration, media.FrameCount);
        if (planned == 0 || interval <= 0) return;

        // Полоска кончается там же, где зелёная полоса готовности: её ширина и есть
        // ответ на вопрос «сколько ролика уже в кэше».
        var full = ThumbStripArea.ActualWidth;
        var covered = Math.Clamp(_builtFraction, 0, 1);
        var width = Math.Floor(full * covered);
        ThumbStripBox.Width = width;

        var height = ThumbStripArea.ActualHeight > 2 ? ThumbStripArea.ActualHeight - 2 : 44;
        var aspect = media.Height > 0 ? media.Width / (double)media.Height : 16 / 9.0;
        var cellWidth = Math.Max(height * aspect, 1);

        var cells = Math.Max((int)Math.Round(width / cellWidth), 1);

        // Ни ширина, ни набор файлов не изменились — перекладывать нечего.
        if (cells == _thumbShown && _thumbFiles.Count == _thumbSource) return;
        _thumbShown = cells;
        _thumbSource = _thumbFiles.Count;

        ThumbStrip.Columns = cells;
        ThumbStrip.Children.Clear();

        for (var i = 0; i < cells; i++)
        {
            // Время середины клетки в пределах собранной части → снятый там кадр.
            var time = (i + 0.5) / cells * covered * media.Duration.TotalSeconds;
            var index = Math.Clamp((int)(time / interval), 0, Math.Min(planned, _thumbFiles.Count) - 1);

            var source = index >= 0 && index < _thumbFiles.Count ? LoadThumbnail(_thumbFiles[index]) : null;
            if (source is null) continue;

            ThumbStrip.Children.Add(new Image
            {
                Source = source,
                Stretch = Stretch.UniformToFill,
                Margin = new Thickness(0, 0, 1, 0)
            });
        }

        UpdateThumbHead();
    }

    private void ClearThumbnails()
    {
        _thumbFiles = [];
        _thumbShown = 0;
        _thumbSource = 0;
        _thumbCache.Clear();
        ThumbStrip.Children.Clear();
        ThumbStripBox.Width = 0;
        ThumbStripArea.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Загрузка с <see cref="BitmapCacheOption.OnLoad"/>: файл сразу закрывается,
    /// иначе очистка кэша спотыкалась бы о занятые картинки.
    /// </summary>
    private ImageSource? LoadThumbnail(string path)
    {
        if (_thumbCache.TryGetValue(path, out var cached)) return cached;

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bitmap.UriSource = new Uri(path);
            bitmap.EndInit();
            bitmap.Freeze();

            _thumbCache[path] = bitmap;
            return bitmap;
        }
        catch (Exception)
        {
            // Файл ещё дописывается ffmpeg'ом — покажем его на следующем обновлении.
            return null;
        }
    }

    private void UpdateThumbHead()
    {
        if (ThumbStripArea.Visibility != Visibility.Visible) return;
        if (_backend.Media is not { } media || media.FrameCount <= 1) return;

        SetThumbHead(_backend.FrameIndex / (double)(media.FrameCount - 1));
    }

    private void SetThumbHead(double ratio)
    {
        var offset = Math.Clamp(ratio, 0, 1) * Math.Max(ThumbStripArea.ActualWidth - ThumbHead.Width, 0);
        ThumbHead.Margin = new Thickness(offset, 0, 0, 0);
    }

    private void ThumbStrip_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        LayoutThumbnails();
        UpdateThumbHead();
    }

    /// <summary>По полоске миниатюр можно и щёлкать, и вести playhead перетаскиванием.</summary>
    private void ThumbStrip_MouseDown(object sender, MouseButtonEventArgs e)
    {
        ThumbStripArea.CaptureMouse();
        _thumbDragging = true;
        SeekToStripPoint(e.GetPosition(ThumbStripArea).X);
    }

    private void ThumbStrip_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_thumbDragging || e.LeftButton != MouseButtonState.Pressed) return;
        SeekToStripPoint(e.GetPosition(ThumbStripArea).X);
    }

    private void ThumbStrip_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_thumbDragging) return;

        _thumbDragging = false;
        ThumbStripArea.ReleaseMouseCapture();

        // На медленном источнике во время перетаскивания кадр не декодировался —
        // показываем его теперь, по отпусканию кнопки.
        if (!LiveScrub) SeekToStripPoint(e.GetPosition(ThumbStripArea).X, force: true);
    }

    private void SeekToStripPoint(double x, bool force = false)
    {
        if (_backend.Media is not { } media || ThumbStripArea.ActualWidth <= 0) return;

        var ratio = Math.Clamp(x / ThumbStripArea.ActualWidth, 0, 1);
        var frame = (long)Math.Round(ratio * Math.Max(media.FrameCount - 1, 0));


        if (force || LiveScrub)
        {
            ScrubToFrame(frame);
            return;
        }

        // Медленный источник: кадр не декодируем, но цель показываем — и подписями, и playhead'ом.
        ShowFrameLabels(frame);
        SetThumbHead(ratio);
    }

    // ---------- форматирование ----------

    private string Remaining() => _buildEta.Length > 0 ? $" · ~{_buildEta}" : "";

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
