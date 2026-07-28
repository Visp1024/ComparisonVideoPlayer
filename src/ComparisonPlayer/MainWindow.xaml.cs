using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ComparisonPlayer.Playback;
using Microsoft.Win32;

namespace ComparisonPlayer;

/// <summary>
/// Окно одиночного плеера: кадр, боковая панель сведений, шкала позиции и транспорт.
/// Вся работа с видео идёт через <see cref="IPlaybackBackend"/> — окно не знает,
/// декодируется кадр напрямую или будет взят из кэша (фаза 4).
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>Расширения, которые принимаем перетаскиванием и показываем в диалоге.</summary>
    private static readonly string[] VideoExtensions =
        [".mp4", ".mkv", ".mov", ".avi", ".ts", ".m4v", ".webm", ".wmv", ".mpg", ".mpeg"];

    /// <summary>
    /// Прямой декод: он же владеет Player'ом, привязанным к FlyleafHost.
    /// Живёт всё время работы окна, даже когда кадры идут из кэша.
    /// </summary>
    private readonly FlyleafBackend _flyleaf = new();

    /// <summary>
    /// Действующий движок: либо прямой декод, либо <see cref="FrameCacheBackend"/>
    /// поверх него. Всё окно работает только через эту ссылку.
    /// </summary>
    private IPlaybackBackend _backend;

    /// <summary>Идёт перетаскивание playhead: seek делаем один раз, отпустив кнопку.</summary>
    private bool _scrubbing;

    /// <summary>Значение шкалы меняет код, а не пользователь — реагировать на это не нужно.</summary>
    private bool _syncingScrub;

    /// <summary>Идёт декодирование кадра при перетаскивании: следующие движения мыши пропускаем.</summary>
    private bool _seeking;

    public MainWindow()
    {
        InitializeComponent();

        _backend = _flyleaf;
        Subscribe(_backend);

        Loaded += OnLoaded;
        Closed += OnClosed;

        // FlyleafHost выводит кадр в собственные окна (поверхность и накладка),
        // и когда фокус уходит туда, события клавиатуры до главного окна не доходят.
        // Классовый обработчик ловит их в любом окне приложения — и ровно один раз.
        EventManager.RegisterClassHandler(typeof(Window), Keyboard.PreviewKeyDownEvent,
            new KeyEventHandler((_, e) => e.Handled = HandleKey(e.Key)));

        DragOver += OnDragOver;
        Drop += OnDrop;
    }

    /// <summary>Подписка окна на движок: при смене режима переезжает на новый.</summary>
    private void Subscribe(IPlaybackBackend backend)
    {
        backend.PositionChanged += OnBackendPositionChanged;
        backend.StateChanged += OnBackendStateChanged;
    }

    private void Unsubscribe(IPlaybackBackend backend)
    {
        backend.PositionChanged -= OnBackendPositionChanged;
        backend.StateChanged -= OnBackendStateChanged;
    }

    private void OnBackendPositionChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(UpdatePosition);
    private void OnBackendStateChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(UpdateState);

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        VideoHost.Player = _flyleaf.Player;
        InitCacheUi();

        // Накладка — отдельное окно, перетаскивание из главного окна там не ловится.
        OverlayRoot.DragOver += OnDragOver;
        OverlayRoot.Drop += OnDrop;

        Osd.Visibility = App.Settings.ShowOverlay ? Visibility.Visible : Visibility.Collapsed;

        UpdateState();
        Status(string.IsNullOrEmpty(AppEnv.FFmpegDir)
            ? "FFmpeg не найден — открыть файл не получится"
            : "файл не открыт");

        if (App.StartupFile is { } file)
            OpenFile(file);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        App.Settings.ShowOverlay = Osd.Visibility == Visibility.Visible;
        App.Settings.Save();

        CancelBuild();
        if (!ReferenceEquals(_backend, _flyleaf)) _backend.Dispose();
        _flyleaf.Dispose();
    }

    // ---------- команды ----------

    private void Open_Click(object sender, RoutedEventArgs e) => OpenWithDialog();
    private void Close_Click(object sender, RoutedEventArgs e) => CloseFile();
    private void PlayPause_Click(object sender, RoutedEventArgs e) => _backend.TogglePlayPause();
    private void StepNext_Click(object sender, RoutedEventArgs e) => _backend.StepForward();
    private void StepPrev_Click(object sender, RoutedEventArgs e) => _backend.StepBack();
    private void ToStart_Click(object sender, RoutedEventArgs e) => _backend.SeekToFrame(0);
    private void Info_Click(object sender, RoutedEventArgs e) => ToggleInfoPanel();

    /// <summary>
    /// Панель сведений — накладка поверх кадра, а не постоянная колонка: при открытии
    /// она ничего не двигает, а свёрнутая отдаёт всю ширину изображению. Каждый запуск
    /// начинается со свёрнутой панели.
    /// </summary>
    private void ToggleInfoPanel()
    {
        var open = InfoPanel.Visibility == Visibility.Visible;
        if (!open && CachePanel.Visibility == Visibility.Visible) ToggleCachePanel();

        InfoPanel.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
        BtnInfo.Foreground = (Brush)FindResource(open ? "TextBrush" : "AccentBrush");
        BtnInfo.BorderBrush = (Brush)FindResource(open ? "LineBrush" : "AccentDim");
        Status(open ? "панель сведений свёрнута (I)" : "панель сведений открыта (I)");
    }

    private bool HandleKey(Key key)
    {
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var step = shift ? 10 : 1;

        switch (key)
        {
            case Key.O when ctrl: OpenWithDialog(); return true;
            case Key.Space: _backend.TogglePlayPause(); return true;
            case Key.Right: _backend.StepForward(step); return true;
            case Key.Left: _backend.StepBack(step); return true;
            case Key.Home: _backend.SeekToFrame(0); return true;
            case Key.End when _backend.Media is { } m: _backend.SeekToFrame(m.FrameCount - 1); return true;
            case Key.T: ToggleOverlay(); return true;
            case Key.I: ToggleInfoPanel(); return true;
            case Key.C: ToggleCachePanel(); return true;
            default: return false;
        }
    }

    private void ToggleOverlay()
    {
        var visible = Osd.Visibility == Visibility.Visible;
        Osd.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
        App.Settings.ShowOverlay = !visible;
        Status(visible ? "таймкод поверх кадра выключен (T)" : "таймкод поверх кадра включён (T)");
    }

    private void OpenWithDialog()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Открыть видео",
            Filter = $"Видео|{string.Join(";", VideoExtensions.Select(x => "*" + x))}|Все файлы|*.*",
            InitialDirectory = Directory.Exists(App.Settings.LastFolder) ? App.Settings.LastFolder : null
        };

        if (dlg.ShowDialog(this) == true)
            OpenFile(dlg.FileName);
    }

    private void OpenFile(string path)
    {
        // Открытие всегда начинается с прямого декода: решение о кэше принимается
        // после того, как файл открылся и стали известны кодек и число кадров.
        CancelBuild();
        ResetCacheState();
        UseDirectBackend();

        var res = _backend.Open(path);
        if (!res.Success)
        {
            Status($"не открылся {Path.GetFileName(path)}: {res.Error}");
            return;
        }

        App.Settings.LastFolder = Path.GetDirectoryName(path);
        App.Settings.Save();

        var m = _backend.Media!;
        Status($"открыт {m.FileName} — {m.Codec} {m.Width}×{m.Height}, {m.Fps:F3} fps" +
               (m.HardwareAcceleration ? ", аппаратный декод" : ", программный декод"));

        // Замер шага назад и сборка кэша — после того, как первый кадр уже на экране.
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () => DecideCache(path));
    }

    private void CloseFile()
    {
        if (!_backend.IsOpen) return;
        var name = _backend.Media!.FileName;

        CancelBuild();
        _backend.Close();
        UseDirectBackend();
        ResetCacheState();

        Status($"закрыт {name}");
    }

    // ---------- перетаскивание файла ----------

    private void OnDragOver(object sender, DragEventArgs e)
    {
        var path = DroppedVideo(e);
        e.Effects = path is null ? DragDropEffects.None : DragDropEffects.Copy;
        e.Handled = true;
        HighlightDropZone(path);
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var path = DroppedVideo(e);
        ResetDropZone();

        if (path is null)
        {
            Status("формат не поддерживается — перетащите видеофайл");
            return;
        }

        OpenFile(path);
    }

    /// <summary>Путь к перетаскиваемому видео либо null, если это не поддерживаемый файл.</summary>
    private static string? DroppedVideo(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return null;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } files) return null;

        var path = files[0];
        if (!File.Exists(path)) return null;   // каталог плеер не открывает

        return VideoExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)
            ? path
            : null;
    }

    private void HighlightDropZone(string? path)
    {
        if (_backend.IsOpen || path is null) return;

        DropFrame.BorderBrush = (Brush)FindResource("AccentBrush");
        DropFrame.Background = new SolidColorBrush(Color.FromArgb(0x10, 0xF2, 0xA1, 0x3C));
        DropTitle.Text = "Отпустите, чтобы открыть";
        DropTitle.Foreground = (Brush)FindResource("AccentBrush");
        DropHint.Text = Path.GetFileName(path);
    }

    private void ResetDropZone()
    {
        DropFrame.BorderBrush = (Brush)FindResource("LineBrush");
        DropFrame.Background = Brushes.Transparent;
        DropTitle.Text = "Перетащите видео сюда";
        DropTitle.Foreground = (Brush)FindResource("TextBrush");
        DropHint.Text = "или нажмите «Открыть файл» · mp4, mkv, mov, avi, ts";
    }

    // ---------- шкала позиции ----------

    private void Scrub_MouseDown(object sender, MouseButtonEventArgs e) => _scrubbing = true;

    private void Scrub_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_scrubbing) return;
        _scrubbing = false;
        if (_backend.IsOpen) _backend.SeekToFrame((long)Scrub.Value);
    }

    private void Scrub_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingScrub || !_backend.IsOpen) return;

        if (!_scrubbing)
        {
            _backend.SeekToFrame((long)Scrub.Value);
            return;
        }

        // Пока playhead тащат, кадр показываем сразу — но только если источник
        // быстрый (кэш или all-intra). На long-GOP исходнике seek занимает около
        // секунды, и декод на каждое движение мыши сделал бы перетаскивание
        // неуправляемым: там кадр показываем, отпустив кнопку.
        ShowFrameLabels((long)Scrub.Value);
        if (LiveScrub) ScrubToFrame((long)Scrub.Value);
    }

    /// <summary>
    /// Можно ли листать кадры прямо во время перетаскивания. Критерий тот же,
    /// что и у решения о кэше: замеренный шаг быстрее порога либо кадры идут
    /// из all-intra источника (кэш, ProRes и подобные).
    /// </summary>
    private bool LiveScrub
    {
        get
        {
            if (_backend.Media is not { } media) return false;
            if (media.FromCache || StepSpeedProbe.IsAllIntra(media)) return true;
            return _sourceStepMs > 0 && _sourceStepMs <= App.Settings.StepBackThresholdMs;
        }
    }

    /// <summary>
    /// Переход на кадр во время перетаскивания. Seek синхронный, поэтому пока
    /// декодируется один кадр, соседние события мыши пропускаем — иначе очередь
    /// seek'ов растёт и playhead отстаёт от курсора.
    /// </summary>
    private void ScrubToFrame(long frame)
    {
        if (_seeking) return;

        _seeking = true;
        try { _backend.SeekToFrame(frame); }
        finally { _seeking = false; }
    }

    // ---------- обновление интерфейса ----------

    private void UpdateState()
    {
        var open = _backend.IsOpen;
        var m = _backend.Media;

        EmptyState.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
        BtnClose.IsEnabled = open;
        BtnPrev.IsEnabled = BtnNext.IsEnabled = BtnPlay.IsEnabled = BtnStart.IsEnabled = open;
        Scrub.IsEnabled = open;
        FrameBadge.Visibility = open ? Visibility.Visible : Visibility.Hidden;
        BtnPlay.Content = _backend.IsPlaying ? "❚❚" : "▶";

        Title = open ? $"ComparisonVideoPlayer — {m!.FileName}" : "ComparisonVideoPlayer";

        _syncingScrub = true;
        Scrub.Maximum = open ? Math.Max(m!.FrameCount - 1, 1) : 1;
        _syncingScrub = false;

        InfoFile.Text = open ? m!.FileName : "—";
        InfoFile.ToolTip = open ? m!.FilePath : null;
        InfoCodec.Text = open ? m!.Codec : "—";
        InfoSize.Text = open ? $"{m!.Width}×{m.Height}" : "—";
        InfoFps.Text = open ? m!.Fps.ToString("F3") : "—";
        InfoDuration.Text = open ? Timecode(m!.Duration) : "—";
        InfoFrames.Text = open ? m!.FrameCount.ToString() : "—";

        InfoSource.Text = !open ? "—" : m!.FromCache ? "кэша" : "исходника";
        InfoSource.Foreground = (Brush)FindResource(open && m!.FromCache ? "OkBrush" : "TextBrush");
        UpdateProxyNote();

        InfoRate.Text = open ? (m!.IsVariableFrameRate ? "VFR" : "CFR") : "—";
        InfoRate.Foreground = open && m!.IsVariableFrameRate
            ? (Brush)FindResource("WarnBrush")
            : (Brush)FindResource("OkBrush");
        if (!open) InfoRate.Foreground = (Brush)FindResource("TextBrush");
        InfoVfrNote.Visibility = open && m!.IsVariableFrameRate ? Visibility.Visible : Visibility.Collapsed;

        UpdateTicks();
        UpdatePosition();
    }

    private void UpdatePosition()
    {
        var m = _backend.Media;
        var pos = _backend.Position;

        TxtTime.Text = Timecode(pos);
        TxtDuration.Text = m is null ? "/ --:--:--.---" : "/ " + Timecode(m.Duration);

        if (m is null)
        {
            TxtFrame.Text = "";
            OsdTime.Text = "--:--:--.---";
            OsdFrame.Text = "";
            return;
        }

        ShowFrameLabels(_backend.FrameIndex);

        if (!_scrubbing)
        {
            _syncingScrub = true;
            Scrub.Value = Math.Clamp(_backend.FrameIndex, Scrub.Minimum, Scrub.Maximum);
            _syncingScrub = false;
        }

        UpdateThumbHead();
    }

    /// <summary>Подписи таймкода и номера кадра — и в транспорте, и поверх изображения.</summary>
    private void ShowFrameLabels(long frame)
    {
        var m = _backend.Media;
        if (m is null) return;

        var dragging = _scrubbing || _thumbDragging;
        var time = dragging && m.Fps > 0 ? TimeSpan.FromSeconds(frame / m.Fps) : _backend.Position;
        var approx = m.IsVariableFrameRate ? "≈" : "";

        TxtTime.Text = Timecode(time);
        TxtFrame.Text = $"кадр {approx}{frame} / {Math.Max(m.FrameCount - 1, 0)}";
        OsdTime.Text = Timecode(time);
        OsdFrame.Text = $"кадр {approx}{frame}";
    }

    private void UpdateTicks()
    {
        var m = _backend.Media;
        TextBlock[] ticks = [Tick0, Tick1, Tick2, Tick3, Tick4];

        for (var i = 0; i < ticks.Length; i++)
        {
            ticks[i].Text = m is null
                ? ""
                : ShortTimecode(TimeSpan.FromTicks((long)(m.Duration.Ticks * (i / (double)(ticks.Length - 1)))));
        }
    }

    private void Status(string message) => TxtStatus.Text = message;

    private static string Timecode(TimeSpan t) => t.ToString(@"hh\:mm\:ss\.fff");
    private static string ShortTimecode(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");
}
