using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ComparisonPlayer.Chrome;
using ComparisonPlayer.Playback;
using ComparisonPlayer.Timeline;
using ComparisonPlayer.Tracks;
using FlyleafLib.Controls.WPF;
using Microsoft.Win32;

namespace ComparisonPlayer;

/// <summary>Что показано в области кадра.</summary>
public enum LayoutMode
{
    /// <summary>Оба кадра рядом — основной режим сравнения.</summary>
    Side,

    /// <summary>Только трек A во всю ширину.</summary>
    OnlyA,

    /// <summary>Только трек B во всю ширину.</summary>
    OnlyB
}

/// <summary>Что показывает боковая панель.</summary>
internal enum SideMode
{
    None,
    Info,
    Cache
}

/// <summary>
/// Окно сравнения: два кадра, боковая панель сведений и кэша, таймлайн с двумя
/// дорожками и общий транспорт. Всё воспроизведение идёт через <see cref="SyncEngine"/>:
/// окно не командует плеерами напрямую, иначе треки разъезжались бы.
/// </summary>
public partial class MainWindow : AppWindow
{
    /// <summary>
    /// Расширения, которые принимаем перетаскиванием и показываем в диалоге. Список общий
    /// с регистрацией типов файлов (задача #13): расходиться им нельзя — иначе «Открыть с
    /// помощью» приводило бы в плеер файл, который тот сам открыть отказывается.
    /// </summary>
    private static string[] VideoExtensions => FileAssociations.VideoExtensions;

    // Плееры и транспорт создаются не в конструкторе, а сразу после первого показа
    // окна (задача #31): до этого момента окно уже нарисовано, но ещё пустое.
    private PlayerTrack _a = null!;
    private PlayerTrack _b = null!;
    private SyncEngine _sync = null!;

    /// <summary>Активный трек: ему адресованы открытие файла, отрезок и назначение мастера.</summary>
    private TrackId _active = TrackId.A;

    private LayoutMode _layout = LayoutMode.Side;
    private SideMode _side = SideMode.None;

    /// <summary>
    /// Таблетки раскладки и мастер-трека (задача #32) выставляются и из кода — тогда
    /// событие Checked приходит по нашей же правке, и обрабатывать его нельзя.
    /// </summary>
    private bool _syncingSegments;

    /// <summary>Какой трек показывает боковая панель.</summary>
    private TrackId _sideTrack = TrackId.A;

    /// <summary>Идёт перетаскивание playhead: на медленном источнике seek делаем, отпустив кнопку.</summary>
    private bool _scrubbing;

    /// <summary>Идёт декодирование кадра при перетаскивании: следующие движения мыши пропускаем.</summary>
    private bool _seeking;

    /// <summary>
    /// Играл ли плеер, когда взялись за playhead. К концу перетаскивания об этом уже
    /// не спросить: живое листание кадров само ставит паузу на каждом движении мыши.
    /// </summary>
    private bool _scrubWasPlaying;

    /// <summary>Повторять отрезок при воспроизведении (клавиша L). Состояние переживает перезапуск.</summary>
    private bool _loop;

    /// <summary>Выбранная скорость воспроизведения; движок при смене режима её подхватывает.</summary>
    private double _speed = 1;

    /// <summary>Переключатели панели расставляет код — реагировать на это не нужно.</summary>
    private bool _syncingSideUi;

    /// <summary>Метка времени последнего обработанного нажатия: отсекает повтор из окон вывода.</summary>
    private int _lastKeyStamp = -1;

    public MainWindow()
    {
        StartupTrace.Mark("win-ctor");
        InitializeComponent();
        StartupTrace.Mark("xaml");

        Closed += OnClosed;
        ContentRendered += OnFirstFrame;
    }

    /// <summary>
    /// Первый показ окна. Всё, без чего окно уже можно нарисовать, отложено сюда
    /// (задача #31): движок, плееры, вывод кадра и открытие файлов — это две трети
    /// времени до окна, а увидеть их раньше самого окна всё равно нельзя. Ввод при
    /// этом не теряется: сообщения ждут в очереди того же потока и разбираются, когда
    /// окно уже готово.
    /// </summary>
    private void OnFirstFrame(object? sender, EventArgs e)
    {
        ContentRendered -= OnFirstFrame;
        StartupTrace.Mark("shown");

        if (!App.StartEngine(this)) return;
        StartupTrace.Mark("engine");

        _a = new PlayerTrack(TrackId.A);
        _b = new PlayerTrack(TrackId.B);
        StartupTrace.Mark("players");

        InitPlayback();
        StartupTrace.Mark("ready");

        AttachVideoHost(PaneA, OverlayA, _a);
        AttachVideoHost(PaneB, OverlayB, _b);
        StartupTrace.Mark("hosts");

        OpenStartupFiles();
        StartupTrace.Mark("files");
        StartupTrace.Flush();
    }

    /// <summary>
    /// Свести треки под общий транспорт и подготовить интерфейс к работе: всё, чему
    /// нужны готовые плееры. До этого момента окно уже нарисовано, но пустое.
    /// </summary>
    private void InitPlayback()
    {
        _sync = new SyncEngine(_a, _b);
        _sync.PositionChanged += (_, _) => Dispatcher.BeginInvoke(UpdatePosition);
        _sync.StateChanged += (_, _) => Dispatcher.BeginInvoke(UpdateState);

        // Коррекцию дрейфа видно в строке состояния: расхождение двух декодеров —
        // вещь, которую надо уметь проверить, а не принимать на веру.
        _sync.Corrected += (_, ms) => Dispatcher.BeginInvoke(() =>
            Status($"ведомый трек подтянут: расхождение {ms:+0;-0} мс"));

        // FlyleafHost выводит кадр в собственные окна (поверхность и накладка),
        // и когда фокус уходит туда, события клавиатуры до главного окна не доходят.
        // Классовый обработчик ловит их в любом окне приложения — и ровно один раз.
        // Сочетания с Alt приходят как Key.System, а настоящая клавиша лежит в SystemKey —
        // без этого Alt+стрелки (сдвиг трека) до окна не доходили вовсе.
        //
        // Каждый FlyleafHost добавляет свои окна вывода, и с двумя треками одно и то же
        // нажатие доходило до классового обработчика дважды: Tab переключал трек туда и
        // обратно, а Ctrl+I открывал и тут же закрывал панель. Отсекаем повтор по метке
        // времени события — у одного нажатия она одна.
        EventManager.RegisterClassHandler(typeof(Window), Keyboard.PreviewKeyDownEvent,
            new KeyEventHandler((_, e) =>
            {
                if (e.Timestamp == _lastKeyStamp)
                {
                    e.Handled = true;
                    return;
                }

                _lastKeyStamp = e.Timestamp;
                e.Handled = HandleKey(e.Key == Key.System ? e.SystemKey : e.Key);
            }));

        // Колесо (фаза 5) приходится ловить тем же способом и по той же причине:
        // над кадром его перехватывают окна вывода FlyleafHost, поэтому берём событие
        // в любом окне приложения и даже уже помеченное обработанным.
        EventManager.RegisterClassHandler(typeof(Window), Mouse.PreviewMouseWheelEvent,
            new MouseWheelEventHandler(OnAnyWheel), true);

        DragOver += (s, e) => OnDragOver(s, e, null);
        DragLeave += OnDragLeave;
        Drop += (s, e) => OnDrop(s, e, null);

        InitRemote();
        InitCacheUi();
        InitTimeline();
        InitCompact();
        InitAudio();
        InitScale();
        InitFullScreen();
        ApplyLayout();

        // Накладка — отдельное окно, перетаскивание из главного окна там не ловится.
        OverlayA.DragOver += (s, args) => OnDragOver(s, args, TrackId.A);
        OverlayA.DragLeave += OnDragLeave;
        OverlayA.Drop += (s, args) => OnDrop(s, args, TrackId.A);
        OverlayB.DragOver += (s, args) => OnDragOver(s, args, TrackId.B);
        OverlayB.DragLeave += OnDragLeave;
        OverlayB.Drop += (s, args) => OnDrop(s, args, TrackId.B);

        _showOsd = App.Settings.ShowOverlay;
        ApplyOverlay();

        UpdateState();
        Status(string.IsNullOrEmpty(AppEnv.FFmpegDir)
            ? "FFmpeg не найден — открыть файл не получится"
            : "файл не открыт");
    }

    /// <summary>
    /// Подставить в панель трека вывод кадра. В разметке этот элемент стоил около 200 мс
    /// холодного старта, поэтому создаётся кодом: разметка панели становится
    /// накладкой хоста — тем же самым, чем была внутри него в XAML.
    /// </summary>
    private void AttachVideoHost(Border pane, Grid overlay, PlayerTrack track)
    {
        var host = new FlyleafHost
        {
            VideoBackground = (Brush)FindResource("VideoBrush"),
            KeyBindings = AvailableWindows.None,
            OpenOnDrop = AvailableWindows.None,
            SwapOnDrop = AvailableWindows.None,
            ToggleFullScreenOnDoubleClick = AvailableWindows.None
        };

        // В коэффициент заполнения кадра (задача #28) входит соотношение сторон области
        // вывода, а оно меняется и от размера окна, и от раскладки «рядом / только A».
        host.SizeChanged += (_, _) => ApplyScale();

        // Накладку сначала отцепляем от панели: у элемента WPF один родитель.
        pane.Child = null;
        host.Content = overlay;
        pane.Child = host;

        host.Player = track.Flyleaf.Player;
    }

    /// <summary>
    /// Что показать при запуске: файлы командной строки, а без них — прошлая сессия.
    /// Файлы из командной строки сильнее сессии: их открыли осознанно именно сейчас.
    /// </summary>
    private void OpenStartupFiles()
    {
        if (App.StartupFile is { } file)
        {
            var opened = OpenFile(_a, file);

            if (App.StartupFileB is { } second)
                opened |= OpenFile(_b, second);

            ApplyStartupLayout();
            if (opened) AutoPlayAfterOpen();
            return;
        }

        RestoreLastSession();
        ApplyStartupLayout();
    }

    private PlayerTrack Active => _sync.Track(_active);
    private PlayerTrack SideTrack => _sync.Track(_sideTrack);

    /// <summary>
    /// Вид кадра при запуске (настройка, задача #17). «Как в прошлый раз» ничего не
    /// навязывает: вид уже пришёл из восстановленной сессии, а без неё это «рядом».
    /// Применяется после открытия файлов — иначе сессия перебила бы выбранный вид.
    /// </summary>
    private void ApplyStartupLayout()
    {
        LayoutMode? wanted = App.Settings.StartupLayout switch
        {
            StartupLayoutMode.Side => LayoutMode.Side,
            StartupLayoutMode.OnlyA => LayoutMode.OnlyA,
            StartupLayoutMode.OnlyB => LayoutMode.OnlyB,
            _ => null
        };

        if (wanted is not { } layout || layout == _layout) return;

        _layout = layout;
        ApplyLayout();
    }

    /// <summary>
    /// Пустить воспроизведение сразу после открытия ролика, если так настроено
    /// (задача #17). Ждём фоновой очереди: решение о кэше меряет шаг назад точными
    /// переходами, и начатое до замера воспроизведение он всё равно сбил бы.
    /// </summary>
    private void AutoPlayAfterOpen()
    {
        if (!App.Settings.AutoPlayOnOpen) return;

        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (!_sync.IsOpen || _sync.IsPlaying) return;
            PlayFromHere();
        });
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        // Окно закрылось, не дойдя до готовности: так бывает, когда движок не поднялся
        // и приложение гасит себя само. Разбирать тогда нечего — и нечем.
        if (_sync is null) return;

        StopShuttle();

        // Курсор мог остаться спрятанным полноэкранным режимом — счётчик системный,
        // и вернуть его обязаны мы.
        ShowFsCursor(true);
        SaveLastSession();

        App.Settings.ShowOverlay = _showOsd;
        App.Settings.LoopSegment = _loop;
        App.Settings.SnapToFrames = Timeline.SnapEnabled;
        App.Settings.Volume = _sync.Volume;
        App.Settings.Muted = _sync.Muted;
        App.Settings.Save();

        foreach (var track in _sync.Tracks)
        {
            CancelBuild(track);
            CancelThumbnails(track);
        }

        StopRemote();

        _sync.Dispose();
        _a.Dispose();
        _b.Dispose();
    }

    // ---------- команды ----------

    private void OpenA_Click(object sender, RoutedEventArgs e) => OpenWithDialog(_a);
    private void OpenB_Click(object sender, RoutedEventArgs e) => OpenWithDialog(_b);
    private void Close_Click(object sender, RoutedEventArgs e) => CloseFile(Active);
    private void PlayPause_Click(object sender, RoutedEventArgs e) => TogglePlayPause();
    private void StepNext_Click(object sender, RoutedEventArgs e) => StepBy(StepSize);
    private void StepPrev_Click(object sender, RoutedEventArgs e) => StepBy(-StepSize);
    private void ShuttleBack_Click(object sender, RoutedEventArgs e) => ShuttleBack();
    private void ShuttleForward_Click(object sender, RoutedEventArgs e) => ShuttleForward();
    private void Info_Click(object sender, RoutedEventArgs e) => ToggleSide(SideMode.Info);
    private void Cache_Click(object sender, RoutedEventArgs e) => ToggleSide(SideMode.Cache);
    private void SideClose_Click(object sender, RoutedEventArgs e) => ToggleSide(_side);
    private void SettingsOpen_Click(object sender, RoutedEventArgs e) => OpenSettings();
    private void SettingsKeys_Click(object sender, RoutedEventArgs e) => OpenSettings(SettingsWindow.KeysPage);

    private void ToStart_Click(object sender, RoutedEventArgs e)
    {
        StopShuttle();
        JumpToFrame(_sync.SegmentInFrame);
    }

    /// <summary>
    /// Флажок в меню шестерёнки: настройку меняют по ходу просмотра, поэтому она
    /// сохраняется сразу, без «применить». Тот же флажок есть в окне настроек.
    /// </summary>
    private void PauseOnSeek_Click(object sender, RoutedEventArgs e)
    {
        App.Settings.PauseOnSeek = MnuPauseOnSeek.IsChecked;
        App.Settings.Save();

        Status(App.Settings.PauseOnSeek
            ? "переход по таймлайну ставит на паузу"
            : "переход по таймлайну не прерывает воспроизведение");
    }

    private void File_Click(object sender, RoutedEventArgs e) => DropMenu(BtnFile);
    private void Settings_Click(object sender, RoutedEventArgs e) => DropMenu(BtnSettings);

    /// <summary>
    /// Меню кнопки панели (задача #32): «Файл» и шестерёнка прячут за собой по три-четыре
    /// команды каждая — отдельная кнопка на каждую занимала всю панель. Меню открывается
    /// левой кнопкой и под кнопкой, а не там, где системное контекстное.
    /// </summary>
    private static void DropMenu(Button button)
    {
        if (button.ContextMenu is not { } menu) return;

        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    /// <summary>Шаг стрелки и кнопок транспорта: с Shift — крупный (величина в настройках).</summary>
    private int StepSize =>
        Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? Math.Max(App.Settings.BigStepFrames, 1) : 1;

    /// <summary>Шаг кадрами по команде пользователя — он же прекращает шаттл.</summary>
    private void StepBy(int frames)
    {
        if (frames == 0) return;

        StopShuttle();

        if (frames > 0) _sync.StepForward(frames);
        else _sync.StepBack(-frames);
    }

    private void TogglePlayPause()
    {
        if (!_sync.IsOpen) return;

        StopShuttle();

        if (_sync.IsPlaying)
        {
            _sync.Pause();
            return;
        }

        PlayFromHere();
    }

    /// <summary>
    /// Пустить воспроизведение с текущего кадра. Оно всегда идёт внутри отрезка мастера:
    /// запуск с кадра вне его начинается с начала отрезка, иначе кнопка «play» на
    /// отрезанном хвосте не делала бы ничего.
    /// </summary>
    private void PlayFromHere()
    {
        if (_sync.PositionFrame < _sync.SegmentInFrame || _sync.PositionFrame >= _sync.SegmentOutFrame)
            SeekFrame(_sync.SegmentInFrame);

        _sync.Play(SeekTrackFrame);
    }

    private bool HandleKey(Key key)
    {
        // Пока правят поле скорости или сдвига, клавиши принадлежат ему: иначе пробел
        // ставил бы плеер на паузу вместо ввода, а стрелки уводили бы кадр вместо курсора.
        if (Keyboard.FocusedElement is TextBox) return false;

        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
        var step = shift ? Math.Max(App.Settings.BigStepFrames, 1) : 1;

        switch (key)
        {
            case Key.O when ctrl && shift: OpenWithDialog(_b); return true;
            case Key.O when ctrl: OpenWithDialog(_a); return true;
            case Key.Space: TogglePlayPause(); return true;

            // Шаттл монтажного пульта: J — назад, K — стоп, L — вперёд; повторное
            // нажатие удваивает скорость (фаза 5).
            case Key.J: ShuttleBack(); return true;
            case Key.K: ShuttleStop(); return true;
            case Key.L when !ctrl: ShuttleForward(); return true;

            // Alt+стрелки правят сдвиг второго трека покадрово — мышью так не попасть.
            case Key.Right when alt: NudgeOffset(step); return true;
            case Key.Left when alt: NudgeOffset(-step); return true;
            case Key.D0 or Key.NumPad0 when alt: ResetOffset(); return true;

            case Key.Right: StepBy(step); return true;
            case Key.Left: StepBy(-step); return true;

            // Home/End работают по отрезку — он и есть рабочая область; края шкалы под Shift.
            case Key.Home: StopShuttle(); JumpToFrame(shift ? 0 : _sync.SegmentInFrame); return true;
            case Key.End: StopShuttle(); JumpToFrame(shift ? _sync.LastFrame : _sync.SegmentOutFrame); return true;

            case Key.Tab: SwitchActiveTrack(); return true;
            case Key.V: CycleLayout(); return true;

            // Кадр во весь экран (задача #28); Esc — только выход, в обычном виде
            // он ничего не делает и должен доставаться диалогам.
            case Key.F11: ToggleFullScreen(); return true;
            case Key.Escape when _fullscreen: LeaveFullScreen(); return true;
            case Key.Z: CycleScale(); return true;

            // M назначает мастера, и звук идёт за ним; выключается звук соседним Ctrl+M.
            case Key.M when ctrl: ToggleMute(); return true;
            case Key.M: MakeActiveMaster(); return true;

            case Key.Up when ctrl: NudgeVolume(VolumeStep); return true;
            case Key.Down when ctrl: NudgeVolume(-VolumeStep); return true;

            // Ctrl+T сворачивает таймлайн до полосы, T без модификатора — накладка на кадре.
            case Key.T when ctrl: ToggleCompact(); return true;
            case Key.T: ToggleOverlay(); return true;

            // I и O заняты отрезком: в покадровой работе он нужнее панели сведений,
            // которая переехала на Ctrl+I.
            case Key.I when ctrl: ToggleSide(SideMode.Info); return true;
            case Key.I when shift: ResetSegment(); return true;
            case Key.I: SetSegmentIn(); return true;
            case Key.O: SetSegmentOut(); return true;

            // L отдана шаттлу, поэтому петля переехала на Ctrl+L.
            case Key.L when ctrl: ToggleLoop(); return true;

            case Key.S when ctrl: SaveSessionAs(); return true;
            case Key.S: ToggleSnap(); return true;
            case Key.F: FitTimeline(); return true;
            case Key.OemPlus or Key.Add: ZoomTimeline(ZoomStep); return true;
            case Key.OemMinus or Key.Subtract: ZoomTimeline(1 / ZoomStep); return true;

            case Key.C: ToggleSide(SideMode.Cache); return true;
            case Key.F1: OpenSettings(SettingsWindow.KeysPage); return true;
            case Key.OemComma when ctrl: OpenSettings(); return true;
            default: return false;
        }
    }

    // ---------- треки, layout, панель ----------

    private void SwitchActiveTrack()
    {
        SetActiveTrack(_active == TrackId.A ? TrackId.B : TrackId.A);
        Status($"активный трек: {Active.Letter}" + (Active.IsOpen ? $" — {Active.Media!.FileName}" : " (пуст)"));
    }

    private void SetActiveTrack(TrackId id)
    {
        if (_active == id) return;

        _active = id;

        // Панель следует за активным треком: обычно смотрят именно его.
        _sideTrack = id;
        UpdateState();
    }

    private void MakeActiveMaster() => MakeMaster(_active);

    /// <summary>Щелчок по сегменту таблетки мастер-трека (задача #32).</summary>
    private void Master_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncingSegments) return;

        MakeMaster(ReferenceEquals(sender, SegMasterB) ? TrackId.B : TrackId.A);
    }

    /// <summary>
    /// Назначить мастера. С клавиши (M) им становится активный трек, щелчком по
    /// таблетке — любой: раньше, чтобы сделать мастером второй трек, приходилось
    /// сперва переключать активный.
    /// </summary>
    private void MakeMaster(TrackId id)
    {
        var track = id == TrackId.A ? _a : _b;

        if (!track.IsOpen)
        {
            Status($"трек {track.Letter} пуст — мастером его не сделать");
            UpdateTimelineButtons();
            return;
        }

        if (_sync.MasterId == id)
        {
            Status($"трек {track.Letter} уже мастер: шаг меряется его кадрами");
            return;
        }

        _sync.SetMaster(id);
        SeekFrame(_sync.PositionFrame);
        UpdateState();

        // Звук переехал вместе с мастером — об этом стоит сказать сразу, иначе
        // смена звучащего трека выглядит как самоволие плеера.
        var audio = _sync.HasAudio && !_sync.Muted && _sync.Volume > 0 ? " и звук" : "";
        Status($"мастер — трек {track.Letter}: шаг меряется его кадрами ({track.Fps:0.###} fps){audio}");
    }

    /// <summary>
    /// Переключение раскладки по кругу (V). Без второго ролика выбирать не из чего:
    /// «только B» вело бы в пустой экран, поэтому вместо переключения плеер говорит
    /// об этом в строке состояния (задача #32).
    /// </summary>
    private void CycleLayout()
    {
        if (!BothOpen())
        {
            Status("раскладка нужна двум роликам: второй трек не открыт");
            return;
        }

        SetLayout(_layout switch
        {
            LayoutMode.Side => LayoutMode.OnlyA,
            LayoutMode.OnlyA => LayoutMode.OnlyB,
            _ => LayoutMode.Side
        });
    }

    /// <summary>Щелчок по сегменту таблетки раскладки.</summary>
    private void Layout_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncingSegments) return;

        SetLayout(ReferenceEquals(sender, SegLayoutA) ? LayoutMode.OnlyA
            : ReferenceEquals(sender, SegLayoutB) ? LayoutMode.OnlyB
            : LayoutMode.Side);
    }

    private void SetLayout(LayoutMode mode)
    {
        _layout = mode;

        ApplyLayout();
        Status(_layout switch
        {
            LayoutMode.OnlyA => "показан только трек A (V)",
            LayoutMode.OnlyB => "показан только трек B (V)",
            _ => "кадры рядом (V)"
        });
    }

    private void ApplyLayout()
    {
        // Выбранный трек могли закрыть — тогда раскладка показала бы пустоту.
        var layout = _layout switch
        {
            LayoutMode.OnlyA when !_a.IsOpen && _b.IsOpen => LayoutMode.OnlyB,
            LayoutMode.OnlyB when !_b.IsOpen && _a.IsOpen => LayoutMode.OnlyA,
            _ => _layout
        };

        var showA = layout != LayoutMode.OnlyB;
        var showB = layout != LayoutMode.OnlyA;

        PaneA.Visibility = showA ? Visibility.Visible : Visibility.Collapsed;
        PaneB.Visibility = showB ? Visibility.Visible : Visibility.Collapsed;

        PaneAColumn.Width = showA ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        PaneBColumn.Width = showB ? new GridLength(1, GridUnitType.Star) : new GridLength(0);

        _syncingSegments = true;
        SegLayoutA.IsChecked = layout == LayoutMode.OnlyA;
        SegLayoutB.IsChecked = layout == LayoutMode.OnlyB;
        SegLayoutBoth.IsChecked = layout == LayoutMode.Side;
        _syncingSegments = false;
    }

    /// <summary>Открыть или свернуть боковую панель; открытой всегда одна из двух.</summary>
    private void ToggleSide(SideMode mode)
    {
        _side = _side == mode ? SideMode.None : mode;

        ApplySideVisibility();
        InfoSection.Visibility = _side == SideMode.Info ? Visibility.Visible : Visibility.Collapsed;
        CacheSection.Visibility = _side == SideMode.Cache ? Visibility.Visible : Visibility.Collapsed;
        SideTitle.Text = _side == SideMode.Cache ? "К Э Ш   К А Д Р О В" : "С В Е Д Е Н И Я";

        Highlight(BtnInfo, _side == SideMode.Info);
        Highlight(BtnCache, _side == SideMode.Cache);

        UpdateState();
        Status(_side switch
        {
            SideMode.Info => "панель сведений открыта (Ctrl+I)",
            SideMode.Cache => "панель кэша открыта (C)",
            _ => "панель свёрнута"
        });
    }

    /// <summary>
    /// Боковая панель видна, когда открыта и когда для неё есть место: в полноэкранном
    /// виде (задача #28) кадр занимает экран целиком, а выбор панели переживает выход
    /// из него — возвращаться в свёрнутую панель после Esc человек не просил.
    /// </summary>
    private void ApplySideVisibility() =>
        SidePanel.Visibility = _side != SideMode.None && !_fullscreen
            ? Visibility.Visible
            : Visibility.Collapsed;

    private void SideTab_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncingSideUi || sender is not RadioButton { Tag: string tag }) return;
        if (!Enum.TryParse<TrackId>(tag, out var id) || id == _sideTrack) return;

        _sideTrack = id;
        UpdateState();
    }

    /// <summary>Таймкод и номер кадра поверх изображения (клавиша T).</summary>
    private bool _showOsd = true;

    private void Overlay_Click(object sender, RoutedEventArgs e) => ToggleOverlay();

    private void ToggleOverlay()
    {
        _showOsd = !_showOsd;
        App.Settings.ShowOverlay = _showOsd;

        ApplyOverlay();
        UpdatePosition();
        Status(_showOsd ? "накладка над кадром включена (T)" : "накладка над кадром выключена (T)");
    }

    /// <summary>
    /// Накладка над кадром — плашка с именем файла, таймкодом, номером кадра и ролью
    /// трека. Прячется целиком (задача #32): выключенной оставалась половина плашки,
    /// и «кадр без накладки» получить было нечем. В полноэкранном виде её нет всегда —
    /// там на экране остаётся только кадр (задача #28).
    /// </summary>
    private void ApplyOverlay()
    {
        var shown = _showOsd && !_fullscreen ? Visibility.Visible : Visibility.Collapsed;

        PaneLabelA.Visibility = shown;
        PaneLabelB.Visibility = shown;

        Highlight(BtnOverlay, _showOsd);
    }

    // ---------- открытие файлов ----------

    private void OpenWithDialog(PlayerTrack track)
    {
        var dlg = new OpenFileDialog
        {
            Title = $"Открыть видео в трек {track.Letter}",
            Filter = $"Видео|{string.Join(";", VideoExtensions.Select(x => "*" + x))}|Все файлы|*.*",
            InitialDirectory = Directory.Exists(App.Settings.LastFolder) ? App.Settings.LastFolder : null
        };

        if (dlg.ShowDialog(this) == true && OpenFile(track, dlg.FileName))
            AutoPlayAfterOpen();
    }

    /// <summary>
    /// Открыть ролик в трек. Возвращает, получилось ли: восстановление сессии открывает
    /// два файла подряд и должно знать, что из этого вышло.
    /// </summary>
    /// <param name="quiet">Не писать сообщение об успехе — его напишет вызывающий.</param>
    private bool OpenFile(PlayerTrack track, string path, bool quiet = false)
    {
        StopShuttle();

        // Открытие всегда начинается с прямого декода: решение о кэше принимается
        // после того, как файл открылся и стали известны кодек и число кадров.
        CancelBuild(track);
        CancelThumbnails(track);
        track.ResetCacheState();
        UseDirectBackend(track);

        // Библиотека декодирования — нативный код, и на битом файле она может не вернуть
        // ошибку, а бросить исключение: падать окном приложения из-за одного ролика нельзя.
        OpenResult res;
        try
        {
            res = track.Backend.Open(path);
        }
        catch (Exception ex)
        {
            res = OpenResult.Fail(ex.Message);
        }

        if (!res.Success)
        {
            ShowOpenError(track, path, res.Error);
            return false;
        }

        ClearOpenError(track);
        track.ResetSegment();
        SetActiveTrack(track.Id);

        App.Settings.LastFolder = Path.GetDirectoryName(path);
        App.Settings.Save();

        var m = track.Media!;
        if (!quiet)
            Status($"{track.Letter}: открыт {m.FileName} — {m.Codec} {m.Width}×{m.Height}, {m.Fps:F3} fps" +
                   (m.HardwareAcceleration ? ", аппаратный декод" : ", программный декод"));

        UpdateState();
        SeekFrame(_sync.PositionFrame);

        // Замер шага назад и сборка кэша — после того, как первый кадр уже на экране.
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () => DecideCache(track, path));
        return true;
    }

    private void CloseFile(PlayerTrack track)
    {
        ClearOpenError(track);

        if (!track.IsOpen) return;
        var name = track.Media!.FileName;

        StopShuttle();
        CancelBuild(track);
        CancelThumbnails(track);
        track.Backend.Close();
        UseDirectBackend(track);
        track.ResetCacheState();
        track.Offset = TimeSpan.Zero;

        UpdateState();
        Status($"{track.Letter}: закрыт {name}");
    }

    // ---------- ошибки открытия ----------

    /// <summary>
    /// Отказ показываем там, где ждали кадр: строка состояния под таймлайном при
    /// открытии второго ролика легко остаётся незамеченной, а пустая панель трека —
    /// ровно то место, куда смотрят.
    /// </summary>
    private void ShowOpenError(PlayerTrack track, string path, string error)
    {
        var (title, hint) = track.Id == TrackId.A ? (DropTitleA, DropHintA) : (DropTitleB, DropHintB);
        var name = Path.GetFileName(path);
        var reason = string.IsNullOrWhiteSpace(error) ? "причина неизвестна" : error;

        // Без FFmpeg не открывается ничего, и настоящая причина — именно он.
        if (string.IsNullOrEmpty(AppEnv.FFmpegDir))
            reason += ". Библиотеки FFmpeg не найдены: задайте COMPARISONPLAYER_FFMPEG_DIR " +
                      "или положите их в подкаталог FFmpeg рядом с программой";

        title.Text = $"Не открылся {name}";
        title.Foreground = (Brush)FindResource("WarnBrush");
        hint.Text = reason + " · перетащите сюда другой файл";

        UpdateState();
        Status($"{track.Letter}: не открылся {name} — {reason}");
    }

    /// <summary>Вернуть панели трека обычное приглашение перетащить файл.</summary>
    private void ClearOpenError(PlayerTrack track)
    {
        var (title, hint) = track.Id == TrackId.A ? (DropTitleA, DropHintA) : (DropTitleB, DropHintB);

        title.Text = $"Трек {track.Letter} пуст";
        title.Foreground = (Brush)FindResource("TextBrush");
        hint.Text = track.Id == TrackId.A
            ? "перетащите видео сюда · mp4, mkv, mov, avi, ts"
            : "перетащите сюда второй ролик";
    }

    // ---------- перетаскивание файла ----------

    /// <summary>Подсвеченная сейчас сторона; <c>null</c> — перетаскивания нет.</summary>
    private TrackId? _dropHint;

    /// <summary>
    /// Счётчик событий перетаскивания. Уход курсора с панели приходит раньше, чем
    /// наведение на соседнюю, и снимать подсветку сразу означало бы моргать ею на
    /// каждой границе: гасим отложенно и только если наведения так и не случилось.
    /// </summary>
    private int _dragTick;

    /// <summary>
    /// Куда лёг бы файл, брошенный над стороной <paramref name="hovered"/>. Открытый
    /// ролик перетаскиванием не заменяют, пока второй трек пуст: бросок в плеер с
    /// одним файлом — это заявка на сравнение (задача #35). Когда открыты оба,
    /// заменяется та сторона, над которой держат файл.
    /// </summary>
    private TrackId DropTarget(TrackId hovered) =>
        _a.IsOpen == _b.IsOpen ? hovered : _a.IsOpen ? TrackId.B : TrackId.A;

    private void OnDragOver(object sender, DragEventArgs e, TrackId? hovered)
    {
        // Окно показано раньше, чем подняты плееры (задача #31): до этого брать
        // файл некуда.
        if (_sync is null) return;

        var (path, _) = DroppedVideo(e);
        e.Effects = path is null ? DragDropEffects.None : DragDropEffects.Copy;
        e.Handled = true;

        _dragTick++;

        // Сессия меняет обе стороны разом — подсвечивать одну из них было бы обманом.
        ShowDropHint(path is null || IsSessionFile(path)
            ? null
            : DropTarget(hovered ?? _active));
    }

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        var tick = _dragTick;
        Dispatcher.BeginInvoke(DispatcherPriority.Background,
            () => { if (_dragTick == tick) ShowDropHint(null); });
    }

    private void ShowDropHint(TrackId? target)
    {
        if (_dropHint == target) return;

        _dropHint = target;
        ApplyDropHint();
    }

    private void ApplyDropHint()
    {
        DropTargetA.Visibility = Visibility.Collapsed;
        DropTargetB.Visibility = Visibility.Collapsed;

        if (_dropHint is not { } target) return;

        var track = _sync.Track(target);
        var other = _sync.Track(target == TrackId.A ? TrackId.B : TrackId.A);

        // Раскладка может прятать сторону, в которую файл и уйдёт («только A», а
        // открывается B): подсветка тогда рисуется в видимой панели, а надпись всё
        // равно называет настоящую сторону — иначе бросок выглядел бы промахом.
        var targetVisible = (target == TrackId.A ? PaneA : PaneB).Visibility == Visibility.Visible;
        var inA = target == TrackId.A ? targetVisible : !targetVisible;

        var (box, text) = inA ? (DropTargetA, DropTargetTextA) : (DropTargetB, DropTargetTextB);

        box.BorderBrush = (Brush)FindResource(target == TrackId.A ? "AccentBrush" : "TrackBBrush");
        box.Background = new SolidColorBrush(target == TrackId.A
            ? Color.FromArgb(0x33, 0xF2, 0xA1, 0x3C)
            : Color.FromArgb(0x33, 0x4E, 0xA3, 0xE0));

        text.Text = track.IsOpen ? $"Заменить {track.Letter}"
            : other.IsOpen ? $"Сравнить: открыть в {track.Letter}"
            : $"Открыть в {track.Letter}";

        box.Visibility = Visibility.Visible;
    }

    private void OnDrop(object sender, DragEventArgs e, TrackId? hovered)
    {
        e.Handled = true;
        if (_sync is null) return;

        ShowDropHint(null);

        var (path, error) = DroppedVideo(e);

        if (path is null)
        {
            Status(error);
            return;
        }

        // Сессию открываем как сессию: её файл тоже удобно бросать в окно.
        if (IsSessionFile(path))
        {
            if (Session.Load(path) is { } session) ApplySession(session, $"сессия «{Path.GetFileNameWithoutExtension(path)}»");
            else Status($"не прочитать сессию {Path.GetFileName(path)} — файл повреждён");
            return;
        }

        var wasOpen = _a.IsOpen || _b.IsOpen;

        if (!OpenFile(_sync.Track(DropTarget(hovered ?? _active)), path)) return;

        // Второй ролик в плеере — это и есть сравнение: показываем оба кадра, даже
        // если до броска была раскладка «только A».
        if (wasOpen && BothOpen() && _layout != LayoutMode.Side)
        {
            _layout = LayoutMode.Side;
            ApplyLayout();
        }

        AutoPlayAfterOpen();
    }

    private static bool IsSessionFile(string path) =>
        path.EndsWith(Session.FileExtension, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Путь к перетаскиваемому файлу и причина отказа, если брать его не следует.
    /// Причину важно назвать: «ничего не произошло» на брошенную папку читается как сбой.
    /// </summary>
    private static (string? Path, string Error) DroppedVideo(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return (null, "это не файл — перетащите видео");
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } files)
            return (null, "не удалось прочитать перетаскиваемое");

        var path = files[0];

        if (Directory.Exists(path)) return (null, "это папка — перетащите видеофайл");
        if (!File.Exists(path)) return (null, $"файл не найден: {path}");

        if (path.EndsWith(Session.FileExtension, StringComparison.OrdinalIgnoreCase)) return (path, "");

        return VideoExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)
            ? (path, "")
            : (null, $"формат {Path.GetExtension(path)} не поддерживается — {string.Join(", ", VideoExtensions)}");
    }

    // ---------- таймлайн ----------

    /// <summary>Шаг зума за щелчок колеса и за нажатие «+»/«−».</summary>
    private const double ZoomStep = 1.25;

    private void InitTimeline()
    {
        _loop = App.Settings.LoopSegment;
        Timeline.SnapEnabled = App.Settings.SnapToFrames;
        MnuPauseOnSeek.IsChecked = App.Settings.PauseOnSeek;

        Timeline.ScrubStarted += (_, _) =>
        {
            StopShuttle();
            _scrubbing = true;
            _scrubWasPlaying = _sync.IsPlaying;
        };
        Timeline.ScrubMoved += (_, frame) => TimelineScrub(frame);
        Timeline.ScrubEnded += (_, frame) => TimelineScrubEnd(frame);
        Timeline.TrimDragged += (_, change) => TimelineTrim(change);
        Timeline.OffsetDragged += (_, drag) => TimelineOffset(drag);
        Timeline.TrackActivated += (_, index) => SetActiveTrack(index == 0 ? TrackId.A : TrackId.B);
        Timeline.SegmentResetRequested += (_, index) => ResetSegment(index == 0 ? _a : _b);

        ShowSpeed();
        ShowOffset();
    }

    /// <summary>Собрать для контрола текущее состояние дорожек в кадрах мастер-клока.</summary>
    private void RefreshTimeline()
    {
        var views = new List<TimelineTrackView>();

        foreach (var track in _sync.Tracks)
        {
            var start = _sync.TimelineFrameAt(track.Offset);
            var end = track.IsOpen ? _sync.ToTimeline(track, track.FrameCount) : start;

            views.Add(new TimelineTrackView
            {
                Letter = track.Letter,
                IsMaster = _sync.Master is { } master && ReferenceEquals(master, track),
                IsActive = track.Id == _active,
                IsOpen = track.IsOpen,
                StartFrame = start,
                EndFrame = end,
                InFrame = _sync.ToTimeline(track, track.InFrame),
                OutFrame = _sync.ToTimeline(track, track.OutFrame),
                BuiltFraction = track.BuiltFraction,
                HasThumbnails = track.HasThumbnails,
                ThumbFraction = track.ThumbFraction,
                Aspect = track.Media is { Height: > 0 } m ? m.Width / (double)m.Height : 16 / 9.0,
                ThumbnailProvider = time => ThumbnailAt(track, time)
            });
        }

        Timeline.SetTracks(views, _sync.TimelineFrames, _sync.MasterFps);
        RefreshCompactBar();
        ShowPlayhead(_sync.PositionFrame);
    }

    /// <summary>
    /// Playhead ведут мышью. Кадр показываем сразу, но только если источник быстрый
    /// (кэш или all-intra): на long-GOP исходнике seek занимает около секунды, и декод
    /// на каждое движение мыши сделал бы перетаскивание неуправляемым.
    /// </summary>
    private void TimelineScrub(long frame)
    {
        if (!_sync.IsOpen) return;

        ShowPlayhead(frame);
        _sync.SetPosition(frame);
        ShowFrameLabels();

        if (LiveScrub) ScrubToFrame(frame);
    }

    private void TimelineScrubEnd(long frame)
    {
        _scrubbing = false;

        var wasPlaying = _scrubWasPlaying;
        _scrubWasPlaying = false;

        if (!_sync.IsOpen) return;

        ShowPlayhead(frame);
        SeekFrame(frame);
        ResumeAfterJump(wasPlaying);
    }

    /// <summary>Границу отрезка тащат мышью: контрол работает в кадрах шкалы, трек — в своих.</summary>
    private void TimelineTrim(TrimChange change)
    {
        var track = change.Track == 0 ? _a : _b;
        if (!track.IsOpen) return;

        var local = Math.Clamp(_sync.LocalFrame(track, change.Frame), 0, track.LastFrame);

        if (change.IsIn) track.SetIn(local);
        else track.SetOut(local);

        RefreshTimeline();
        UpdateSegmentText();
    }

    /// <summary>
    /// Клип тащат по дорожке — это выравнивание (PLAN.md §4.3). Кадры не пересчитываем
    /// на каждое движение мыши: при отпускании оба трека встанут на свои места разом.
    /// </summary>
    private void TimelineOffset(OffsetDrag drag)
    {
        var track = drag.Track == 0 ? _a : _b;

        if (drag.Finished)
        {
            SeekFrame(_sync.PositionFrame);
            ShowOffset();
            Status(OffsetMessage());
            return;
        }

        if (!track.IsOpen || !_b.IsOpen || !_a.IsOpen) return;

        ShiftTrack(track, drag.Frames);
        Status(OffsetMessage());
    }

    /// <summary>
    /// Сдвинуть трек и удержать под playhead тот же материал: нормализация пары
    /// может увести начало шкалы, и мастер-время вместе с ним.
    /// </summary>
    private void ShiftTrack(PlayerTrack track, long frames)
    {
        var shift = _sync.ShiftTrack(track, frames);
        if (shift != 0) _sync.SetPosition(_sync.PositionFrame + shift);

        RefreshTimeline();
        ShowOffset();
        UpdateSegmentText();
    }

    private string OffsetMessage()
    {
        var frames = _sync.RelativeOffsetFrames(_b);
        if (frames == 0) return "треки совмещены: сдвиг 0";

        var time = _sync.FrameTime(Math.Abs(frames));
        var sign = frames > 0 ? "+" : "−";
        var who = frames > 0 ? "B отстаёт от A" : "B опережает A";
        return $"сдвиг B: {sign}{Math.Abs(frames)} кадров ({time:mm\\:ss\\.fff}) — {who}";
    }

    // ---------- сдвиг: поле и клавиши ----------

    private void OffsetDown_Click(object sender, RoutedEventArgs e) => NudgeOffset(-1);
    private void OffsetUp_Click(object sender, RoutedEventArgs e) => NudgeOffset(1);
    private void OffsetReset_Click(object sender, RoutedEventArgs e) => ResetOffset();

    /// <summary>Подвинуть трек B относительно A на кадры мастера.</summary>
    private void NudgeOffset(long frames)
    {
        if (!BothOpen())
        {
            Status("сдвиг нужен, когда открыты оба трека");
            return;
        }

        // Выравнивание — работа с дорожками: в свёрнутом виде его результат виден только
        // засечками, поэтому правка сдвига разворачивает таймлайн.
        ExpandForTimeline();

        ShiftTrack(_b, frames);
        SeekFrame(_sync.PositionFrame);
        Status(OffsetMessage());
    }

    private void SetOffset(long frames)
    {
        if (!BothOpen()) return;
        NudgeOffset(frames - _sync.RelativeOffsetFrames(_b));
    }

    private void ResetOffset()
    {
        if (!_sync.IsOpen) return;

        ExpandForTimeline();

        var shift = _sync.ResetOffsets();
        if (shift != 0) _sync.SetPosition(_sync.PositionFrame + shift);

        RefreshTimeline();
        ShowOffset();
        SeekFrame(_sync.PositionFrame);
        Status("сдвиг сброшен: треки начинаются вместе");
    }

    private bool BothOpen() => _a.IsOpen && _b.IsOpen;

    private void Offset_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ApplyTypedOffset();
            Keyboard.ClearFocus();
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Escape) return;

        ShowOffset();
        Keyboard.ClearFocus();
        e.Handled = true;
    }

    private void Offset_LostFocus(object sender, RoutedEventArgs e) => ApplyTypedOffset();

    private void ApplyTypedOffset()
    {
        var text = TxtOffset.Text.Trim().Replace('−', '-').Replace('+', ' ').Trim();

        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var frames))
        {
            SetOffset(frames);
            return;
        }

        Status($"непонятный сдвиг «{TxtOffset.Text}» — оставил прежний");
        ShowOffset();
    }

    private void ShowOffset()
    {
        var frames = BothOpen() ? _sync.RelativeOffsetFrames(_b) : 0;
        TxtOffset.Text = frames > 0 ? $"+{frames}" : frames.ToString(CultureInfo.InvariantCulture);

        var enabled = BothOpen();
        TxtOffset.IsEnabled = BtnOffsetDown.IsEnabled = BtnOffsetUp.IsEnabled = BtnOffsetReset.IsEnabled = enabled;
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
        _sync.Speed = _speed;

        if (changed) Status($"скорость воспроизведения: {SpeedName(clamped)}");
    }

    private void ShowSpeed()
    {
        TxtSpeed.Text = _speed.ToString("0.##", CultureInfo.GetCultureInfo("ru-RU"));
        BtnSpeedDown.IsEnabled = _speed > MinSpeed;
        BtnSpeedUp.IsEnabled = _speed < MaxSpeed;
    }

    private static string SpeedName(double speed) =>
        speed.ToString("0.##", CultureInfo.GetCultureInfo("ru-RU")) + "×";

    // ---------- звук ----------

    /// <summary>Шаг громкости у Ctrl+↑ и Ctrl+↓.</summary>
    private const int VolumeStep = 5;

    /// <summary>Ползунок расставляет код — принимать это за действие пользователя не нужно.</summary>
    private bool _syncingVolumeUi;

    /// <summary>
    /// Звук всегда идёт с мастер-трека (PLAN.md §7.1): при сравнении двух роликов
    /// две дорожки разом — каша, а выбирать звучащий трек отдельно от мастера значит
    /// заводить второй «главный трек» там, где хватает одного.
    /// </summary>
    private void InitAudio()
    {
        _sync.Volume = App.Settings.Volume;
        _sync.Muted = App.Settings.Muted;
        ShowVolume();
    }

    private void Mute_Click(object sender, RoutedEventArgs e) => ToggleMute();

    private void ToggleMute()
    {
        _sync.Muted = !_sync.Muted;
        App.Settings.Muted = _sync.Muted;

        ShowVolume();
        UpdateInfoPanel();
        ShowFrameLabels();
        Status(AudioMessage());
    }

    private void Volume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingVolumeUi) return;
        SetVolume((int)Math.Round(e.NewValue));
    }

    private void NudgeVolume(int delta) => SetVolume(_sync.Volume + delta);

    private void SetVolume(int volume)
    {
        var clamped = Math.Clamp(volume, 0, 100);
        var changed = clamped != _sync.Volume;

        _sync.Volume = clamped;
        App.Settings.Volume = clamped;

        // Прибавлять громкость при выключенном звуке — это попытка его вернуть,
        // а не пожелание сделать тишину погромче.
        if (clamped > 0 && _sync.Muted)
        {
            _sync.Muted = false;
            App.Settings.Muted = false;
        }

        ShowVolume();
        UpdateInfoPanel();
        ShowFrameLabels();
        if (changed) Status(AudioMessage());
    }

    private void ShowVolume()
    {
        // Ползунков два — в транспорте и в свёрнутом подвале; ведут они одну громкость,
        // поэтому расставляются вместе, а не по видимости.
        _syncingVolumeUi = true;
        VolumeSlider.Value = VolumeSliderMini.Value = _sync.Volume;
        _syncingVolumeUi = false;

        var silent = _sync.Muted || _sync.Volume == 0;
        var audible = _sync.HasAudio && !silent;

        BtnMute.Tag = BtnMuteMini.Tag = FindResource(silent ? "IcoMute" : "IcoVolume");
        Highlight(BtnMute, audible);
        Highlight(BtnMuteMini, audible);

        TxtVolume.Text = TxtVolumeMini.Text = !_sync.IsOpen ? "—"
            : !_sync.HasAudio ? "нет"
            : silent ? "выкл"
            : $"{_sync.Volume} %";
    }

    /// <summary>Что происходит со звуком — тем же языком, что и остальная строка состояния.</summary>
    private string AudioMessage()
    {
        if (_sync.AudioTrack is not { } track) return "звук зазвучит, когда откроется трек";

        if (!_sync.HasAudio)
            return CacheBuiltWithoutAudio(track)
                ? $"у мастера {track.Letter} звука нет: кэш собран без дорожки — «Собрать заново» (C) вернёт её"
                : $"у мастера {track.Letter} нет звуковой дорожки";

        if (_sync.Muted) return "звук выключен (Ctrl+M)";
        if (_sync.Volume == 0) return "громкость 0 % — тишина (Ctrl+↑ громче)";

        return $"звук с трека {track.Letter} · громкость {_sync.Volume} %";
    }

    // ---------- зум, снэп, отрезок ----------

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
        // Зум в свёрнутом виде показывать негде — разворачиваем, а не молчим.
        ExpandForTimeline();

        Timeline.Zoom(factor);
        UpdateZoomText();
    }

    private void FitTimeline()
    {
        ExpandForTimeline();

        Timeline.FitAll();
        UpdateZoomText();
        Status("вся шкала в ширину окна (F)");
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
        Status(_loop ? "петля по отрезку включена (Ctrl+L)" : "петля по отрезку выключена (Ctrl+L)");
    }

    // ---------- настройки ----------

    /// <summary>
    /// Окно настроек. Значения, которые меняют поведение открытых треков (режим кэша,
    /// частота прокси), применяются теми же путями, что и переключатели панели «Кэш…»:
    /// одно решение — одна реализация.
    /// </summary>
    /// <param name="page">Раздел, на котором открыть окно; F1 ведёт сразу на шпаргалку по клавишам.</param>
    private void OpenSettings(string? page = null)
    {
        var before = App.Settings;
        var dialog = new SettingsWindow(before, page) { Owner = this };

        if (dialog.ShowDialog() != true) return;

        var changed = dialog.Result;
        var cacheMode = changed.CacheMode;
        var cacheFps = changed.CacheFps;

        // Режим кэша и частоту прокси применяем отдельно и после сохранения остального:
        // они пересобирают кэш и трогают открытые треки.
        changed.CacheMode = before.CacheMode;
        changed.CacheFps = before.CacheFps;

        App.Settings = changed;
        App.Settings.Save();

        _showOsd = changed.ShowOverlay;
        ApplyOverlay();
        _loop = changed.LoopSegment;
        Timeline.SnapEnabled = changed.SnapToFrames;
        MnuPauseOnSeek.IsChecked = changed.PauseOnSeek;

        RbAutoHint.Text = $"строить, если шаг назад медленнее {changed.StepBackThresholdMs} мс";

        UpdateTimelineButtons();
        UpdatePosition();

        if (cacheMode != App.Settings.CacheMode) ApplyCacheMode(cacheMode);
        if (Math.Abs(cacheFps - App.Settings.CacheFps) > 0.001) ApplyCacheFps(cacheFps);

        InitCacheUi();
        Status("настройки сохранены");
    }

    /// <summary>Границы отрезка ставит активный трек — в его собственных кадрах.</summary>
    private void SetSegmentIn()
    {
        if (!Active.IsOpen) return;

        Active.SetIn(Math.Clamp(_sync.LocalFrame(Active, _sync.PositionFrame), 0, Active.LastFrame));
        RefreshTimeline();
        UpdateSegmentText();
        Status($"{Active.Letter}: начало отрезка — кадр {Active.InFrame}");
    }

    private void SetSegmentOut()
    {
        if (!Active.IsOpen) return;

        Active.SetOut(Math.Clamp(_sync.LocalFrame(Active, _sync.PositionFrame), 0, Active.LastFrame));
        RefreshTimeline();
        UpdateSegmentText();
        Status($"{Active.Letter}: конец отрезка — кадр {Active.OutFrame}");
    }

    private void ResetSegment() => ResetSegment(Active);

    private void ResetSegment(PlayerTrack track)
    {
        if (!track.IsOpen) return;

        track.ResetSegment();
        RefreshTimeline();
        UpdateSegmentText();
        Status($"{track.Letter}: отрезок сброшен на весь ролик");
    }

    /// <summary>
    /// Конец отрезка при воспроизведении: с петлёй возвращаемся в начало, без неё
    /// останавливаемся ровно на последнем кадре отрезка. Отрезок задаёт мастер —
    /// ведомый следует за ним, как и на всём остальном транспорте.
    /// </summary>
    private void EnforceSegment()
    {
        if (!_sync.IsPlaying || !_sync.IsOpen || _scrubbing) return;
        if (_sync.PositionFrame < _sync.SegmentOutFrame) return;

        if (_loop)
        {
            SeekFrame(_sync.SegmentInFrame);
            _sync.Play(SeekTrackFrame);
            return;
        }

        StopShuttle();
        _sync.Pause();
        SeekFrame(_sync.SegmentOutFrame);
        Status($"конец отрезка (кадр {_sync.SegmentOutFrame}) — петля выключена (Ctrl+L)");
    }

    private void UpdateTimelineButtons()
    {
        Highlight(BtnSnap, Timeline.SnapEnabled);
        Highlight(BtnLoop, _loop);

        var open = _sync.IsOpen;
        BtnZoomIn.IsEnabled = BtnZoomOut.IsEnabled = BtnFit.IsEnabled = open;
        BtnIn.IsEnabled = BtnOut.IsEnabled = BtnSegReset.IsEnabled = Active.IsOpen;

        MasterPill.IsEnabled = open;
        SegMasterA.IsEnabled = _a.IsOpen;
        SegMasterB.IsEnabled = _b.IsOpen;

        _syncingSegments = true;
        SegMasterA.IsChecked = _sync.MasterId == TrackId.A && _a.IsOpen;
        SegMasterB.IsChecked = _sync.MasterId == TrackId.B && _b.IsOpen;
        _syncingSegments = false;

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
        if (!_sync.IsOpen)
        {
            TxtZoom.Text = "";
            return;
        }

        var ratio = Timeline.ZoomRatio;
        TxtZoom.Text = ratio < 1.05 ? "вся шкала" : $"1 : {ratio:0.#}";
    }

    private void UpdateSegmentText()
    {
        if (!Active.IsOpen)
        {
            TxtSegment.Text = "";
            return;
        }

        if (Active.IsFullSegment)
        {
            TxtSegment.Text = $"{Active.Letter}: отрезок — весь ролик";
            return;
        }

        var from = Active.TimeOf(Active.InFrame);
        var to = Active.TimeOf(Active.OutFrame);
        TxtSegment.Text =
            $"{Active.Letter}: отрезок {ShortTimecode(from)} – {ShortTimecode(to)} · {Active.SegmentFrames} кадров";
    }

    // ---------- переходы ----------

    /// <summary>
    /// Можно ли листать кадры прямо во время перетаскивания. Критерий тот же, что и у
    /// решения о кэше, но теперь по обоим трекам: если хоть один медленный, живое
    /// листание сделает перетаскивание неуправляемым.
    /// </summary>
    private bool LiveScrub => _sync.OpenTracks.All(IsFastSource);

    private bool IsFastSource(PlayerTrack track)
    {
        if (track.Media is not { } media) return true;
        if (media.FromCache || StepSpeedProbe.IsAllIntra(media)) return true;
        return track.SourceStepMs > 0 && track.SourceStepMs <= App.Settings.StepBackThresholdMs;
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

    /// <summary>Единственный переход на кадр шкалы из интерфейса: движок разведёт его по трекам.</summary>
    private void SeekFrame(long frame) => _sync.SeekToFrame(frame, SeekTrackFrame);

    /// <summary>
    /// Переход по команде пользователя: щелчок по шкале, Home/End, «в начало отрезка».
    /// От <see cref="SeekFrame"/> отличается тем, что уважает настройку «пауза при
    /// переходе»; сам <see cref="SeekFrame"/> вызывают и петля, и шаттл, и сборка кэша —
    /// им воспроизведение восстанавливать не нужно, они ведут его сами.
    /// </summary>
    private void JumpToFrame(long frame)
    {
        var wasPlaying = _sync.IsPlaying;
        SeekFrame(frame);
        ResumeAfterJump(wasPlaying);
    }

    /// <summary>
    /// Продолжить воспроизведение после перехода, если настройка «пауза при переходе»
    /// выключена. Seek движка всегда останавливает плеер (см. FlyleafBackend.SeekToFrame),
    /// поэтому «не ставить на паузу» — это пустить его заново с нового кадра.
    /// </summary>
    private void ResumeAfterJump(bool wasPlaying)
    {
        if (!wasPlaying || App.Settings.PauseOnSeek || !_sync.IsOpen) return;

        _sync.Play(SeekTrackFrame);
    }

    /// <summary>
    /// Переход одного трека на его кадр. Кроме собственно seek следит за границей
    /// собираемого кэша: за ней кадров ещё нет, и играть приходится с исходника,
    /// пока сборка туда не дойдёт (фаза 4).
    /// </summary>
    private void SeekTrackFrame(PlayerTrack track, long frame)
    {
        if (track.Backend is FrameCacheBackend { Entry.Partial: true } cache && frame >= cache.AvailableFrames)
        {
            // Может статься, что кадр уже дописан — перечитываем файл, и только
            // если его действительно ещё нет, возвращаемся на исходник.
            if (frame >= ExtendPartialCache(track, cache))
                PlayFromSource(track, $"{track.Letter}: кадр {frame} ещё не в кэше — играю с исходника");
        }

        track.Backend.SeekToFrame(frame);
    }

    // ---------- обновление интерфейса ----------

    private void UpdateState()
    {
        var open = _sync.IsOpen;

        MnuClose.IsEnabled = Active.IsOpen;
        BtnPrev.IsEnabled = BtnNext.IsEnabled = BtnPlay.IsEnabled = BtnStart.IsEnabled = open;

        // Фигура иконки живёт в Tag (задача #32): ▶ и ❚❚ — одна и та же кнопка.
        var playIcon = FindResource(_sync.IsPlaying ? "IcoPause" : "IcoPlay");
        BtnPlay.Tag = BtnPlayMini.Tag = BtnPlayFs.Tag = playIcon;

        BtnPrevMini.IsEnabled = BtnNextMini.IsEnabled = BtnPlayMini.IsEnabled = open;
        BtnShuttleBackMini.IsEnabled = BtnShuttleFwdMini.IsEnabled = open;

        BtnPrevFs.IsEnabled = BtnNextFs.IsEnabled = BtnPlayFs.IsEnabled = open;
        BtnShuttleBackFs.IsEnabled = BtnShuttleFwdFs.IsEnabled = open;

        EmptyA.Visibility = _a.IsOpen ? Visibility.Collapsed : Visibility.Visible;
        EmptyB.Visibility = _b.IsOpen ? Visibility.Collapsed : Visibility.Visible;

        // Полоска активного трека — часть интерфейса, и в полноэкранном виде (задача #28)
        // её нет вместе со всем остальным: у края экрана она читается как дефект картинки.
        // Между двумя кадрами остаётся тонкий разделитель — иначе они сливаются в один.
        PaneA.BorderThickness = new Thickness(_active == TrackId.A && !_fullscreen ? 2 : 0, 0, 0, 0);
        PaneA.BorderBrush = (Brush)FindResource("AccentBrush");
        PaneB.BorderThickness = new Thickness(_active == TrackId.B && !_fullscreen ? 2 : 1, 0, 0, 0);
        PaneB.BorderBrush = (Brush)FindResource(_active == TrackId.B && !_fullscreen ? "TrackBBrush" : "LineBrush");

        PaneNameA.Text = _a.IsOpen ? _a.Media!.FileName : "";
        PaneNameB.Text = _b.IsOpen ? _b.Media!.FileName : "";

        FrameBadgeA.Visibility = _a.IsOpen ? Visibility.Visible : Visibility.Collapsed;
        FrameBadgeB.Visibility = _b.IsOpen ? Visibility.Visible : Visibility.Collapsed;

        UpdateTrackIndication();

        Title = _sync.OpenTracks.Any()
            ? "CVP — " + string.Join(" · ", _sync.OpenTracks.Select(t => $"{t.Letter}: {t.Media!.FileName}"))
            : "CVP";

        UpdateTitleBar();

        _syncingSideUi = true;
        TabA.IsChecked = _sideTrack == TrackId.A;
        TabB.IsChecked = _sideTrack == TrackId.B;
        _syncingSideUi = false;

        RefreshTimeline();
        UpdateTimelineButtons();
        ShowOffset();
        _sync.ApplySpeed();

        // Звук привязан к мастеру и к движку трека, а меняются оба здесь же.
        _sync.ApplyAudio();
        ShowVolume();

        UpdateInfoPanel();
        UpdateModeBadges();
        UpdateCachePanel();
        UpdatePosition();
    }

    /// <summary>Открыт ровно один ролик.</summary>
    private bool SingleTrack => _a.IsOpen ^ _b.IsOpen;

    /// <summary>
    /// Буква трека что-то различает, только когда открыты оба ролика: одному кадру на
    /// экране метка «A» ничего не сообщает, а место занимает (задача #32).
    /// </summary>
    private bool ShowTrackLetters => BothOpen();

    /// <summary>
    /// Пометки треков (задача #32). При одном открытом ролике исчезают буква над кадром,
    /// буква в счётчике кадров (её убирает <see cref="ShowTrackLabels"/>), выбор раскладки
    /// и вкладки A / B боковой панели — выбирать в них не из чего.
    /// </summary>
    private void UpdateTrackIndication()
    {
        var letters = ShowTrackLetters ? Visibility.Visible : Visibility.Collapsed;
        PaneLetterA.Visibility = letters;
        PaneLetterB.Visibility = letters;

        var pair = BothOpen() ? Visibility.Visible : Visibility.Collapsed;
        LayoutPill.Visibility = pair;
        SideTabs.Visibility = pair;

        // Панель показывала бы прочерки по закрытому треку — переводим её на открытый.
        if (SingleTrack) _sideTrack = _a.IsOpen ? TrackId.A : TrackId.B;
    }

    /// <summary>Что открыто — в титульную полосу (задача #21).</summary>
    private void UpdateTitleBar() => Bar.ShowFiles(
        _a.IsOpen ? _a.Media!.FileName : null, _a.IsOpen ? _a.Media!.FilePath : null,
        _b.IsOpen ? _b.Media!.FileName : null, _b.IsOpen ? _b.Media!.FilePath : null);

    private void UpdateInfoPanel()
    {
        var track = SideTrack;
        var open = track.IsOpen;
        var m = track.Media;

        InfoFile.Text = open ? m!.FileName : "—";
        InfoFile.ToolTip = open ? m!.FilePath : null;
        InfoCodec.Text = open ? m!.Codec : "—";
        InfoSize.Text = open ? $"{m!.Width}×{m.Height}" : "—";
        InfoFps.Text = open ? m!.Fps.ToString("F3") : "—";
        InfoDuration.Text = open ? Timecode(m!.Duration) : "—";
        InfoFrames.Text = open ? m!.FrameCount.ToString() : "—";

        InfoSource.Text = !open ? "—" : m!.FromCache ? "кэша" : "исходника";
        InfoSource.Foreground = (Brush)FindResource(open && m!.FromCache ? "OkBrush" : "TextBrush");

        var master = _sync.Master is { } mt && ReferenceEquals(mt, track);
        var offset = BothOpen() ? _sync.RelativeOffsetFrames(track) : 0;
        InfoRole.Text = !open ? "—"
            : master ? "мастер"
            : offset == 0 ? "ведомый"
            : $"ведомый, сдвиг {(offset > 0 ? "+" : "")}{offset}";

        var sounds = _sync.AudioTrack is { } audio && ReferenceEquals(audio, track);
        InfoAudio.Text = !open ? "—"
            : !track.Backend.HasAudio ? (CacheBuiltWithoutAudio(track) ? "нет — кэш без звука" : "нет дорожки")
            : !sounds ? "есть, приглушён (ведомый)"
            : _sync.Muted || _sync.Volume == 0 ? "есть, выключен (Ctrl+M)"
            : $"звучит · {_sync.Volume} %";

        UpdateProxyNote(track);

        InfoRate.Text = open ? (m!.IsVariableFrameRate ? "VFR" : "CFR") : "—";
        InfoRate.Foreground = !open
            ? (Brush)FindResource("TextBrush")
            : (Brush)FindResource(m!.IsVariableFrameRate ? "WarnBrush" : "OkBrush");
        InfoVfrNote.Visibility = open && m!.IsVariableFrameRate ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdatePosition()
    {
        TxtTime.Text = TxtTimeMini.Text = TxtTimeFs.Text = Timecode(_sync.PositionTime);
        TxtDuration.Text = _sync.IsOpen ? "/ " + Timecode(_sync.Duration) : "/ --:--:--.---";
        TxtDurationMini.Text = TxtDurationFs.Text = TxtDuration.Text;

        ShowFrameLabels();

        if (!_scrubbing) ShowPlayhead(_sync.PositionFrame);

        EnforceSegment();
    }

    /// <summary>
    /// Подписи таймкода и номера кадра — и в транспорте, и поверх изображения.
    /// У каждого трека свой номер кадра: при сдвиге и разных fps это разные числа.
    /// </summary>
    private void ShowFrameLabels()
    {
        ShowTrackLabels(_a, TxtFrameA, FrameBadgeA, PaneOsdA, PaneMasterA, NoFrameA, NoFrameHintA,
            TxtFrameAMini, TxtFrameAFs);
        ShowTrackLabels(_b, TxtFrameB, FrameBadgeB, PaneOsdB, PaneMasterB, NoFrameB, NoFrameHintB,
            TxtFrameBMini, TxtFrameBFs);
    }

    private void ShowTrackLabels(PlayerTrack track, TextBlock badgeText, UIElement badge,
        TextBlock osd, TextBlock role, UIElement noFrame, TextBlock noFrameHint,
        TextBlock miniText, TextBlock fsText)
    {
        if (!track.IsOpen)
        {
            badgeText.Text = "";
            miniText.Text = fsText.Text = "";
            osd.Text = "";
            role.Text = "";
            noFrame.Visibility = Visibility.Collapsed;
            return;
        }

        var frame = _sync.DisplayFrame(track);
        var approx = track.Media!.IsVariableFrameRate ? "≈" : "";

        // Ролик открыт один — буква трека в счётчике кадров лишняя (задача #32).
        var letter = ShowTrackLetters ? track.Letter + " " : "";

        badgeText.Text = frame is { } f
            ? $"{letter}{approx}{f} / {Math.Max(track.FrameCount - 1, 0)}"
            : $"{letter}—";
        badge.Opacity = frame is null ? 0.45 : 1;

        // В компактном подвале и в полноэкранной полосе места меньше: номер кадра
        // без общего числа.
        miniText.Text = fsText.Text = frame is { } mini
            ? $"{letter}{approx}{mini}"
            : $"{letter}—";

        osd.Text = _showOsd && frame is { } shown
            ? $"кадр {approx}{shown} · {Timecode(track.TimeOf(shown))}"
            : "";

        var isMaster = _sync.Master is { } master && ReferenceEquals(master, track);
        var offset = BothOpen() ? _sync.RelativeOffsetFrames(track) : 0;

        // Отметка звука именно у кадра: иначе непонятно, который из двух роликов слышно.
        var sounds = isMaster && _sync.HasAudio && !_sync.Muted && _sync.Volume > 0;

        role.Text = isMaster
            ? $"мастер · {track.Fps:0.###} fps" + (sounds ? " · звук" : "")
            : $"{(offset > 0 ? "+" : "")}{offset} кадров · {track.Fps:0.###} fps";

        // Вне материала (или вне отрезка) кадра нет: показываем это прямо,
        // а не замораживаем крайний кадр — в покадровом сравнении это обман.
        noFrame.Visibility = frame is null ? Visibility.Visible : Visibility.Collapsed;

        if (frame is null)
        {
            var local = _sync.LocalFrame(track, _sync.PositionFrame);
            noFrameHint.Text = local < track.InFrame
                ? $"материал {track.Letter} начинается позже"
                : $"материал {track.Letter} здесь уже кончился";
        }
    }

    private void Status(string message) => TxtStatus.Text = message;

    private static string Timecode(TimeSpan t) => t.ToString(@"hh\:mm\:ss\.fff");

    private static string ShortTimecode(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");
}
