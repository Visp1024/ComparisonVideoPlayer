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

        BuildTitle.Text = progress.Stage == BuildStage.Proxy ? "Строится кэш кадров" : "Строятся миниатюры";
        BuildProgressBar.Value = progress.Percent;
        BuildPercent.Text = $"{progress.Percent:F0} %";
        BuildEta.Text = progress.Eta > TimeSpan.Zero ? $"~{Eta(progress.Eta)}" : "";

        BuildTitle.ToolTip = $"{progress.Frame} / {progress.Total} кадров" +
                             (progress.Speed > 0 ? $" · {progress.Speed:F2}× реального времени" : "");

        // Собранная часть на отдельной полосе под шкалой: видно, докуда шаг уже будет мгновенным.
        BuiltLine.Visibility = progress.Stage == BuildStage.Proxy ? Visibility.Visible : Visibility.Collapsed;
        BuiltFill.Width = BuiltLine.ActualWidth * Math.Clamp(progress.Percent / 100.0, 0, 1);

        // Миниатюры показываем по мере появления; последний файл может быть ещё недописан.
        if (progress.Stage == BuildStage.Thumbnails && _cacheKey is { } key)
        {
            var dir = Path.Combine(_cacheStore.DirectoryFor(key), "thumbs");
            if (Directory.Exists(dir))
            {
                var files = Directory.GetFiles(dir, "*.jpg");
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                if (files.Length > 1) ShowThumbnails(files[..^1]);
            }
        }

        UpdateModeBadge();
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
                Status("сборка кэша отменена — играю с исходника");
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
                Status($"кэш не собрался: {message}");
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
        _cacheEntry = entry;
        _cacheStore.Touch(entry);

        _backend.SeekToFrame(frame);
        if (wasPlaying) _backend.Play();

        // На прокси шаг назад стоит миллисекунды — замер честный и почти бесплатный.
        _cacheStepMs = StepSpeedProbe.Measure(_backend, _backend.Media!);
        _backend.SeekToFrame(frame);

        ShowThumbnails(entry.ThumbnailFiles());
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
        var source = media.FilePath;

        UseDirectBackend();

        var res = _flyleaf.Open(source);
        if (!res.Success)
        {
            Status($"не открылся исходник: {res.Error}");
            return;
        }

        _flyleaf.SeekToFrame(frame);
        UpdateState();
        UpdateModeBadge();
        Status(message);
    }

    // ---------- индикаторы ----------

    private void ShowBuildBar(bool visible)
    {
        BuildBar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        BtnBuildCancel.IsEnabled = visible;

        if (visible) return;

        BuildProgressBar.Value = 0;
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
        else if (_buildCts is not null)
        {
            text = $"сборка кэша {_buildPercent:F0} %";
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

    private void ShowThumbnails(IReadOnlyList<string> files)
    {
        _thumbFiles = files;
        LayoutThumbnails();
    }

    /// <summary>
    /// Раскладка полоски: миниатюр может быть больше, чем влезает по ширине,
    /// поэтому берём равномерную выборку — по одной на клетку в ширину кадра.
    /// Пересчитывается при изменении размера окна.
    /// </summary>
    private void LayoutThumbnails()
    {
        if (_thumbFiles.Count == 0)
        {
            ClearThumbnails();
            return;
        }

        ThumbStripBox.Visibility = Visibility.Visible;

        var height = ThumbStripBox.ActualHeight > 2 ? ThumbStripBox.ActualHeight - 2 : 44;
        var aspect = _backend.Media is { Height: > 0 } m ? m.Width / (double)m.Height : 16 / 9.0;
        var width = Math.Max(height * aspect, 1);

        var fit = Math.Max((int)(ThumbStripBox.ActualWidth / width), 1);
        var take = Math.Min(fit, _thumbFiles.Count);

        // Ни ширина, ни набор файлов не изменились — перекладывать нечего.
        if (take == _thumbShown && _thumbFiles.Count == _thumbSource) return;
        _thumbSource = _thumbFiles.Count;

        ThumbStrip.Columns = take;
        ThumbStrip.Children.Clear();

        for (var i = 0; i < take; i++)
        {
            // Середина i-й доли ролика: полоска остаётся равномерной при любом числе клеток.
            var index = (int)((i + 0.5) * _thumbFiles.Count / take);
            index = Math.Clamp(index, 0, _thumbFiles.Count - 1);

            if (LoadThumbnail(_thumbFiles[index]) is not { } source) continue;

            ThumbStrip.Children.Add(new Image
            {
                Source = source,
                Stretch = Stretch.UniformToFill,
                Margin = new Thickness(0, 0, 1, 0)
            });
        }

        _thumbShown = take;
        UpdateThumbHead();
    }

    private void ClearThumbnails()
    {
        _thumbFiles = [];
        _thumbShown = 0;
        _thumbCache.Clear();
        ThumbStrip.Children.Clear();
        ThumbStripBox.Visibility = Visibility.Collapsed;
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
        if (ThumbStripBox.Visibility != Visibility.Visible) return;
        if (_backend.Media is not { } media || media.FrameCount <= 1) return;

        SetThumbHead(_backend.FrameIndex / (double)(media.FrameCount - 1));
    }

    private void SetThumbHead(double ratio)
    {
        var offset = Math.Clamp(ratio, 0, 1) * Math.Max(ThumbStripBox.ActualWidth - ThumbHead.Width, 0);
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
        ThumbStripBox.CaptureMouse();
        _thumbDragging = true;
        SeekToStripPoint(e.GetPosition(ThumbStripBox).X);
    }

    private void ThumbStrip_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_thumbDragging || e.LeftButton != MouseButtonState.Pressed) return;
        SeekToStripPoint(e.GetPosition(ThumbStripBox).X);
    }

    private void ThumbStrip_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_thumbDragging) return;

        _thumbDragging = false;
        ThumbStripBox.ReleaseMouseCapture();

        // На медленном источнике во время перетаскивания кадр не декодировался —
        // показываем его теперь, по отпусканию кнопки.
        if (!LiveScrub) SeekToStripPoint(e.GetPosition(ThumbStripBox).X, force: true);
    }

    private void SeekToStripPoint(double x, bool force = false)
    {
        if (_backend.Media is not { } media || ThumbStripBox.ActualWidth <= 0) return;

        var ratio = Math.Clamp(x / ThumbStripBox.ActualWidth, 0, 1);
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
