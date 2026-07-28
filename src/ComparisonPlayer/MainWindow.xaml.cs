using System.Globalization;
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

    /// <summary>Идёт перетаскивание playhead: на медленном источнике seek делаем, отпустив кнопку.</summary>
    private bool _scrubbing;

    /// <summary>Идёт декодирование кадра при перетаскивании: следующие движения мыши пропускаем.</summary>
    private bool _seeking;

    /// <summary>Повторять отрезок при воспроизведении (клавиша L). Состояние переживает перезапуск.</summary>
    private bool _loop;

    /// <summary>Выбранная скорость воспроизведения; движок при смене режима её подхватывает.</summary>
    private double _speed = 1;

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
        InitTimeline();

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
        App.Settings.LoopSegment = _loop;
        App.Settings.SnapToFrames = Timeline.SnapEnabled;
        App.Settings.Save();

        CancelBuild();
        if (!ReferenceEquals(_backend, _flyleaf)) _backend.Dispose();
        _flyleaf.Dispose();
    }

    // ---------- команды ----------

    private void Open_Click(object sender, RoutedEventArgs e) => OpenWithDialog();
    private void Close_Click(object sender, RoutedEventArgs e) => CloseFile();
    private void PlayPause_Click(object sender, RoutedEventArgs e) => TogglePlayPause();
    private void StepNext_Click(object sender, RoutedEventArgs e) => _backend.StepForward();
    private void StepPrev_Click(object sender, RoutedEventArgs e) => _backend.StepBack();
    private void ToStart_Click(object sender, RoutedEventArgs e) => SeekFrame(Timeline.InFrame);
    private void Info_Click(object sender, RoutedEventArgs e) => ToggleInfoPanel();

    /// <summary>
    /// Воспроизведение всегда идёт внутри отрезка: запуск с кадра вне его начинается
    /// с начала отрезка, иначе кнопка «play» на отрезанном хвосте не делала бы ничего.
    /// </summary>
    private void TogglePlayPause()
    {
        if (!_backend.IsPlaying && _backend.IsOpen && Timeline.IsOpen
            && (_backend.FrameIndex < Timeline.InFrame || _backend.FrameIndex >= Timeline.OutFrame))
            SeekFrame(Timeline.InFrame);

        _backend.TogglePlayPause();
    }

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
        // Пока правят поле скорости, клавиши принадлежат ему: иначе пробел ставил бы
        // плеер на паузу вместо ввода, а стрелки уводили бы кадр вместо курсора.
        if (Keyboard.FocusedElement is TextBox) return false;

        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var step = shift ? 10 : 1;

        switch (key)
        {
            case Key.O when ctrl: OpenWithDialog(); return true;
            case Key.Space: TogglePlayPause(); return true;
            case Key.Right: _backend.StepForward(step); return true;
            case Key.Left: _backend.StepBack(step); return true;

            // Home/End работают по отрезку — он и есть рабочая область; края ролика под Shift.
            case Key.Home: SeekFrame(shift ? 0 : Timeline.InFrame); return true;
            case Key.End when _backend.Media is { } m:
                SeekFrame(shift ? m.FrameCount - 1 : Timeline.OutFrame);
                return true;

            case Key.T: ToggleOverlay(); return true;

            // I и O заняты отрезком: в покадровой работе он нужнее панели сведений,
            // которая переехала на Ctrl+I.
            case Key.I when ctrl: ToggleInfoPanel(); return true;
            case Key.I when shift: ResetSegment(); return true;
            case Key.I: SetSegmentIn(); return true;
            case Key.O: SetSegmentOut(); return true;
            case Key.L: ToggleLoop(); return true;
            case Key.S: ToggleSnap(); return true;
            case Key.F: FitTimeline(); return true;
            case Key.OemPlus or Key.Add: ZoomTimeline(ZoomStep); return true;
            case Key.OemMinus or Key.Subtract: ZoomTimeline(1 / ZoomStep); return true;

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

    // ---------- таймлайн ----------

    /// <summary>Шаг зума за щелчок колеса и за нажатие «+»/«−».</summary>
    private const double ZoomStep = 1.25;

    private void InitTimeline()
    {
        _loop = App.Settings.LoopSegment;
        Timeline.SnapEnabled = App.Settings.SnapToFrames;

        Timeline.ThumbnailProvider = ThumbnailAt;
        Timeline.ScrubStarted += (_, _) => _scrubbing = true;
        Timeline.ScrubMoved += (_, frame) => TimelineScrub(frame);
        Timeline.ScrubEnded += (_, frame) => TimelineScrubEnd(frame);
        Timeline.SegmentChanged += (_, _) => UpdateSegmentText();

        UpdateTimelineButtons();
        ShowSpeed();
    }

    /// <summary>
    /// Playhead ведут мышью. Кадр показываем сразу, но только если источник быстрый
    /// (кэш или all-intra): на long-GOP исходнике seek занимает около секунды, и декод
    /// на каждое движение мыши сделал бы перетаскивание неуправляемым — там кадр
    /// декодируется по отпусканию кнопки (поведение фазы 1).
    /// </summary>
    private void TimelineScrub(long frame)
    {
        if (!_backend.IsOpen) return;

        Timeline.SetPosition(frame);
        ShowFrameLabels(frame);
        if (LiveScrub) ScrubToFrame(frame);
    }

    private void TimelineScrubEnd(long frame)
    {
        _scrubbing = false;
        if (!_backend.IsOpen) return;

        Timeline.SetPosition(frame);
        SeekFrame(frame);
    }

    // ---------- скорость воспроизведения ----------

    /// <summary>Шаг кнопок «−» и «+» у поля скорости.</summary>
    private const double SpeedStep = 0.25;

    private const double MinSpeed = 0.25;
    private const double MaxSpeed = 8;

    private void SpeedDown_Click(object sender, RoutedEventArgs e) => SetSpeed(_speed - SpeedStep);
    private void SpeedUp_Click(object sender, RoutedEventArgs e) => SetSpeed(_speed + SpeedStep);

    /// <summary>Ввод вручную применяется по Enter; Esc возвращает прежнее значение.</summary>
    private void Speed_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ApplyTypedSpeed();
            Keyboard.ClearFocus();
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Escape) return;

        ShowSpeed();
        Keyboard.ClearFocus();
        e.Handled = true;
    }

    private void Speed_LostFocus(object sender, RoutedEventArgs e) => ApplyTypedSpeed();

    /// <summary>
    /// Разбор введённого значения. Принимаем и точку, и запятую (раскладка у поля
    /// одна, а привычки разные), лишний «×» отбрасываем; мусор молча откатываем.
    /// </summary>
    private void ApplyTypedSpeed()
    {
        var text = TxtSpeed.Text.Trim().TrimEnd('×', 'x', 'X').Replace(',', '.').Trim();

        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var speed) && speed > 0)
        {
            SetSpeed(speed);
            return;
        }

        Status($"непонятная скорость «{TxtSpeed.Text}» — оставил {SpeedName(_speed)}");
        ShowSpeed();
    }

    /// <summary>
    /// Скорость живёт в окне, а не в движке: при переходе на кэш и обратно движок
    /// подменяется, и выбранную скорость надо навязать новому.
    /// </summary>
    private void SetSpeed(double speed)
    {
        var clamped = Math.Clamp(Math.Round(speed, 2), MinSpeed, MaxSpeed);
        var changed = Math.Abs(clamped - _speed) > 0.001;

        _speed = clamped;
        ShowSpeed();
        ApplySpeed();

        if (changed) Status($"скорость воспроизведения: {SpeedName(clamped)}");
    }

    private void ShowSpeed()
    {
        TxtSpeed.Text = _speed.ToString("0.##", CultureInfo.GetCultureInfo("ru-RU"));
        BtnSpeedDown.IsEnabled = _speed > MinSpeed;
        BtnSpeedUp.IsEnabled = _speed < MaxSpeed;
    }

    private void ApplySpeed()
    {
        if (!_backend.IsOpen || Math.Abs(_backend.Speed - _speed) < 0.001) return;
        _backend.Speed = _speed;
    }

    private static string SpeedName(double speed) =>
        speed.ToString("0.##", CultureInfo.GetCultureInfo("ru-RU")) + "×";

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => ZoomTimeline(ZoomStep);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => ZoomTimeline(1 / ZoomStep);
    private void Fit_Click(object sender, RoutedEventArgs e) => FitTimeline();
    private void Snap_Click(object sender, RoutedEventArgs e) => ToggleSnap();
    private void Loop_Click(object sender, RoutedEventArgs e) => ToggleLoop();
    private void SetIn_Click(object sender, RoutedEventArgs e) => SetSegmentIn();
    private void SetOut_Click(object sender, RoutedEventArgs e) => SetSegmentOut();
    private void SegmentReset_Click(object sender, RoutedEventArgs e) => ResetSegment();

    private void ZoomTimeline(double factor)
    {
        Timeline.Zoom(factor);
        UpdateZoomText();
    }

    private void FitTimeline()
    {
        Timeline.FitAll();
        UpdateZoomText();
        Status("весь ролик в ширину окна (F)");
    }

    private void ToggleSnap()
    {
        Timeline.SnapEnabled = !Timeline.SnapEnabled;
        App.Settings.SnapToFrames = Timeline.SnapEnabled;
        UpdateTimelineButtons();
        Status(Timeline.SnapEnabled ? "снэп включён (S)" : "снэп выключен (S)");
    }

    private void ToggleLoop()
    {
        _loop = !_loop;
        App.Settings.LoopSegment = _loop;
        UpdateTimelineButtons();
        Status(_loop ? "петля по отрезку включена (L)" : "петля по отрезку выключена (L)");
    }

    private void SetSegmentIn()
    {
        if (!Timeline.IsOpen) return;
        Timeline.SetIn(_backend.FrameIndex);
        Status($"начало отрезка: кадр {Timeline.InFrame}");
    }

    private void SetSegmentOut()
    {
        if (!Timeline.IsOpen) return;
        Timeline.SetOut(_backend.FrameIndex);
        Status($"конец отрезка: кадр {Timeline.OutFrame}");
    }

    private void ResetSegment()
    {
        if (!Timeline.IsOpen) return;
        Timeline.ResetSegment();
        Status("отрезок сброшен на весь ролик");
    }

    /// <summary>
    /// Конец отрезка при воспроизведении: с петлёй возвращаемся в начало, без неё
    /// останавливаемся ровно на последнем кадре отрезка. Шаг и seek границами не
    /// ограничены — отрезок ограничивает воспроизведение, а не просмотр.
    /// </summary>
    private void EnforceSegment()
    {
        if (!_backend.IsPlaying || !Timeline.IsOpen || _scrubbing) return;
        if (_backend.FrameIndex < Timeline.OutFrame) return;

        if (_loop)
        {
            SeekFrame(Timeline.InFrame);
            _backend.Play();
            return;
        }

        _backend.Pause();
        SeekFrame(Timeline.OutFrame);
        Status($"конец отрезка (кадр {Timeline.OutFrame}) — петля выключена (L)");
    }

    private void UpdateTimelineButtons()
    {
        Highlight(BtnSnap, Timeline.SnapEnabled);
        Highlight(BtnLoop, _loop);

        var open = _backend.IsOpen;
        BtnZoomIn.IsEnabled = BtnZoomOut.IsEnabled = BtnFit.IsEnabled = open;
        BtnIn.IsEnabled = BtnOut.IsEnabled = BtnSegReset.IsEnabled = open;

        UpdateZoomText();
        UpdateSegmentText();
    }

    private void Highlight(Button button, bool on)
    {
        button.Foreground = (Brush)FindResource(on ? "AccentBrush" : "TextBrush");
        button.BorderBrush = (Brush)FindResource(on ? "AccentDim" : "LineBrush");
    }

    private void UpdateZoomText()
    {
        if (!Timeline.IsOpen)
        {
            TxtZoom.Text = "";
            return;
        }

        var ratio = Timeline.ZoomRatio;
        TxtZoom.Text = ratio < 1.05 ? "весь ролик" : $"1 : {ratio:0.#}";
    }

    private void UpdateSegmentText()
    {
        if (!Timeline.IsOpen || _backend.Media is not { } media)
        {
            TxtSegment.Text = "";
            return;
        }

        if (Timeline.IsFullSegment)
        {
            TxtSegment.Text = "отрезок: весь ролик";
            return;
        }

        var from = media.Fps > 0 ? TimeSpan.FromSeconds(Timeline.InFrame / media.Fps) : TimeSpan.Zero;
        var to = media.Fps > 0 ? TimeSpan.FromSeconds(Timeline.OutFrame / media.Fps) : TimeSpan.Zero;
        TxtSegment.Text = $"отрезок {ShortTimecode(from)} – {ShortTimecode(to)} · {Timeline.SegmentFrames} кадров";
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
        try { SeekFrame(frame); }
        finally { _seeking = false; }
    }

    /// <summary>
    /// Единственный переход на кадр из интерфейса. Кроме собственно seek следит
    /// за границей собираемого кэша: за ней кадров ещё нет, и играть приходится
    /// с исходника, пока сборка туда не дойдёт.
    /// </summary>
    private void SeekFrame(long frame)
    {
        if (_backend is FrameCacheBackend { Entry.Partial: true } cache && frame >= cache.AvailableFrames)
        {
            // Может статься, что кадр уже дописан — перечитываем файл, и только
            // если его действительно ещё нет, возвращаемся на исходник.
            if (frame >= ExtendPartialCache(cache))
                PlayFromSource($"кадр {frame} ещё не в кэше — играю с исходника");
        }

        _backend.SeekToFrame(frame);
    }

    // ---------- обновление интерфейса ----------

    private void UpdateState()
    {
        var open = _backend.IsOpen;
        var m = _backend.Media;

        EmptyState.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
        BtnClose.IsEnabled = open;
        BtnPrev.IsEnabled = BtnNext.IsEnabled = BtnPlay.IsEnabled = BtnStart.IsEnabled = open;
        FrameBadge.Visibility = open ? Visibility.Visible : Visibility.Hidden;
        BtnPlay.Content = _backend.IsPlaying ? "❚❚" : "▶";

        Title = open ? $"ComparisonVideoPlayer — {m!.FileName}" : "ComparisonVideoPlayer";

        // Смена материала (в том числе переход на прокси другой частоты) сбрасывает
        // зум и отрезок; тот же ролик контрол узнаёт и ничего не трогает.
        Timeline.SetMedia(m);
        UpdateTimelineButtons();
        ApplySpeed();

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

        if (!_scrubbing) Timeline.SetPosition(_backend.FrameIndex);

        EnforceSegment();
    }

    /// <summary>Подписи таймкода и номера кадра — и в транспорте, и поверх изображения.</summary>
    private void ShowFrameLabels(long frame)
    {
        var m = _backend.Media;
        if (m is null) return;

        var time = _scrubbing && m.Fps > 0 ? TimeSpan.FromSeconds(frame / m.Fps) : _backend.Position;
        var approx = m.IsVariableFrameRate ? "≈" : "";

        TxtTime.Text = Timecode(time);
        TxtFrame.Text = $"кадр {approx}{frame} / {Math.Max(m.FrameCount - 1, 0)}";
        OsdTime.Text = Timecode(time);
        OsdFrame.Text = $"кадр {approx}{frame}";
    }

    private void Status(string message) => TxtStatus.Text = message;

    private static string Timecode(TimeSpan t) => t.ToString(@"hh\:mm\:ss\.fff");
    private static string ShortTimecode(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");
}
