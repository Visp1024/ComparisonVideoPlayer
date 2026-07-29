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

    private readonly PlayerTrack _a = new(TrackId.A);
    private readonly PlayerTrack _b = new(TrackId.B);
    private readonly SyncEngine _sync;

    /// <summary>Активный трек: ему адресованы открытие файла, отрезок и назначение мастера.</summary>
    private TrackId _active = TrackId.A;

    private LayoutMode _layout = LayoutMode.Side;
    private SideMode _side = SideMode.None;

    /// <summary>Какой трек показывает боковая панель.</summary>
    private TrackId _sideTrack = TrackId.A;

    /// <summary>Идёт перетаскивание playhead: на медленном источнике seek делаем, отпустив кнопку.</summary>
    private bool _scrubbing;

    /// <summary>Идёт декодирование кадра при перетаскивании: следующие движения мыши пропускаем.</summary>
    private bool _seeking;

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
        InitializeComponent();

        _sync = new SyncEngine(_a, _b);
        _sync.PositionChanged += (_, _) => Dispatcher.BeginInvoke(UpdatePosition);
        _sync.StateChanged += (_, _) => Dispatcher.BeginInvoke(UpdateState);

        // Коррекцию дрейфа видно в строке состояния: расхождение двух декодеров —
        // вещь, которую надо уметь проверить, а не принимать на веру.
        _sync.Corrected += (_, ms) => Dispatcher.BeginInvoke(() =>
            Status($"ведомый трек подтянут: расхождение {ms:+0;-0} мс"));

        Loaded += OnLoaded;
        Closed += OnClosed;

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

        DragOver += OnDragOver;
        Drop += (s, e) => OnDrop(s, e, _active);

        InitRemote();
    }

    private PlayerTrack Active => _sync.Track(_active);
    private PlayerTrack SideTrack => _sync.Track(_sideTrack);

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        VideoHostA.Player = _a.Flyleaf.Player;
        VideoHostB.Player = _b.Flyleaf.Player;

        InitCacheUi();
        InitTimeline();
        InitCompact();
        InitAudio();
        ApplyLayout();

        // Накладка — отдельное окно, перетаскивание из главного окна там не ловится.
        OverlayA.DragOver += OnDragOver;
        OverlayA.Drop += (s, args) => OnDrop(s, args, TrackId.A);
        OverlayB.DragOver += OnDragOver;
        OverlayB.Drop += (s, args) => OnDrop(s, args, TrackId.B);

        _showOsd = App.Settings.ShowOverlay;

        UpdateState();
        Status(string.IsNullOrEmpty(AppEnv.FFmpegDir)
            ? "FFmpeg не найден — открыть файл не получится"
            : "файл не открыт");

        if (App.StartupFile is { } file)
        {
            var opened = OpenFile(_a, file);

            if (App.StartupFileB is { } second)
                opened |= OpenFile(_b, second);

            ApplyStartupLayout();
            if (opened) AutoPlayAfterOpen();
            return;
        }

        // Файлы из командной строки сильнее сессии: их открыли осознанно именно сейчас.
        RestoreLastSession();
        ApplyStartupLayout();
    }

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
        StopShuttle();
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
    private void Layout_Click(object sender, RoutedEventArgs e) => CycleLayout();
    private void Master_Click(object sender, RoutedEventArgs e) => MakeActiveMaster();
    private void Settings_Click(object sender, RoutedEventArgs e) => OpenSettings();

    private void ToStart_Click(object sender, RoutedEventArgs e)
    {
        StopShuttle();
        SeekFrame(_sync.SegmentInFrame);
    }

    /// <summary>Меню сессии открываем нажатием: отдельная кнопка на каждый пункт заняла бы всю панель.</summary>
    private void Session_Click(object sender, RoutedEventArgs e)
    {
        if (BtnSession.ContextMenu is not { } menu) return;

        menu.PlacementTarget = BtnSession;
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
            case Key.Home: StopShuttle(); SeekFrame(shift ? 0 : _sync.SegmentInFrame); return true;
            case Key.End: StopShuttle(); SeekFrame(shift ? _sync.LastFrame : _sync.SegmentOutFrame); return true;

            case Key.Tab: SwitchActiveTrack(); return true;
            case Key.V: CycleLayout(); return true;

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

    private void MakeActiveMaster()
    {
        if (!Active.IsOpen)
        {
            Status($"трек {Active.Letter} пуст — мастером его не сделать");
            return;
        }

        if (_sync.MasterId == _active)
        {
            Status($"трек {Active.Letter} уже мастер: шаг меряется его кадрами");
            return;
        }

        _sync.SetMaster(_active);
        SeekFrame(_sync.PositionFrame);
        UpdateState();

        // Звук переехал вместе с мастером — об этом стоит сказать сразу, иначе
        // смена звучащего трека выглядит как самоволие плеера.
        var audio = _sync.HasAudio && !_sync.Muted && _sync.Volume > 0 ? " и звук" : "";
        Status($"мастер — трек {Active.Letter}: шаг меряется его кадрами ({Active.Fps:0.###} fps){audio}");
    }

    private void CycleLayout()
    {
        _layout = _layout switch
        {
            LayoutMode.Side => LayoutMode.OnlyA,
            LayoutMode.OnlyA => LayoutMode.OnlyB,
            _ => LayoutMode.Side
        };

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
        var showA = _layout != LayoutMode.OnlyB;
        var showB = _layout != LayoutMode.OnlyA;

        PaneA.Visibility = showA ? Visibility.Visible : Visibility.Collapsed;
        PaneB.Visibility = showB ? Visibility.Visible : Visibility.Collapsed;

        PaneAColumn.Width = showA ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        PaneBColumn.Width = showB ? new GridLength(1, GridUnitType.Star) : new GridLength(0);

        BtnLayout.Content = _layout switch
        {
            LayoutMode.OnlyA => "Только A",
            LayoutMode.OnlyB => "Только B",
            _ => "Рядом"
        };
    }

    /// <summary>Открыть или свернуть боковую панель; открытой всегда одна из двух.</summary>
    private void ToggleSide(SideMode mode)
    {
        _side = _side == mode ? SideMode.None : mode;

        SidePanel.Visibility = _side == SideMode.None ? Visibility.Collapsed : Visibility.Visible;
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

    private void SideTab_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncingSideUi || sender is not RadioButton { Tag: string tag }) return;
        if (!Enum.TryParse<TrackId>(tag, out var id) || id == _sideTrack) return;

        _sideTrack = id;
        UpdateState();
    }

    /// <summary>Таймкод и номер кадра поверх изображения (клавиша T).</summary>
    private bool _showOsd = true;

    private void ToggleOverlay()
    {
        _showOsd = !_showOsd;
        App.Settings.ShowOverlay = _showOsd;
        UpdatePosition();
        Status(_showOsd ? "таймкод поверх кадра включён (T)" : "таймкод поверх кадра выключен (T)");
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

    private void OnDragOver(object sender, DragEventArgs e)
    {
        var (path, _) = DroppedVideo(e);
        e.Effects = path is null ? DragDropEffects.None : DragDropEffects.Copy;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e, TrackId id)
    {
        e.Handled = true;
        var (path, error) = DroppedVideo(e);

        if (path is null)
        {
            Status(error);
            return;
        }

        // Сессию открываем как сессию: её файл тоже удобно бросать в окно.
        if (path.EndsWith(Session.FileExtension, StringComparison.OrdinalIgnoreCase))
        {
            if (Session.Load(path) is { } session) ApplySession(session, $"сессия «{Path.GetFileNameWithoutExtension(path)}»");
            else Status($"не прочитать сессию {Path.GetFileName(path)} — файл повреждён");
            return;
        }

        if (OpenFile(_sync.Track(id), path)) AutoPlayAfterOpen();
    }

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

        Timeline.ScrubStarted += (_, _) =>
        {
            StopShuttle();
            _scrubbing = true;
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
        if (!_sync.IsOpen) return;

        ShowPlayhead(frame);
        SeekFrame(frame);
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

        BtnMute.Content = BtnMuteMini.Content = silent ? "🔇" : "🔊";
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
        _loop = changed.LoopSegment;
        Timeline.SnapEnabled = changed.SnapToFrames;

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
        BtnMaster.IsEnabled = open;
        BtnMaster.Content = _sync.Master is { } master ? $"Мастер: {master.Letter}" : "Мастер: —";

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

        BtnClose.IsEnabled = Active.IsOpen;
        BtnPrev.IsEnabled = BtnNext.IsEnabled = BtnPlay.IsEnabled = BtnStart.IsEnabled = open;
        BtnPlay.Content = _sync.IsPlaying ? "❚❚" : "▶";

        BtnPrevMini.IsEnabled = BtnNextMini.IsEnabled = BtnPlayMini.IsEnabled = open;
        BtnShuttleBackMini.IsEnabled = BtnShuttleFwdMini.IsEnabled = open;
        BtnPlayMini.Content = BtnPlay.Content;

        EmptyA.Visibility = _a.IsOpen ? Visibility.Collapsed : Visibility.Visible;
        EmptyB.Visibility = _b.IsOpen ? Visibility.Collapsed : Visibility.Visible;

        PaneA.BorderThickness = new Thickness(_active == TrackId.A ? 2 : 0, 0, 0, 0);
        PaneA.BorderBrush = (Brush)FindResource("AccentBrush");
        PaneB.BorderThickness = new Thickness(_active == TrackId.B ? 2 : 1, 0, 0, 0);
        PaneB.BorderBrush = (Brush)FindResource(_active == TrackId.B ? "TrackBBrush" : "LineBrush");

        PaneNameA.Text = _a.IsOpen ? _a.Media!.FileName : "";
        PaneNameB.Text = _b.IsOpen ? _b.Media!.FileName : "";

        FrameBadgeA.Visibility = _a.IsOpen ? Visibility.Visible : Visibility.Collapsed;
        FrameBadgeB.Visibility = _b.IsOpen ? Visibility.Visible : Visibility.Collapsed;
        ModeBadgeB.Visibility = _b.IsOpen ? Visibility.Visible : Visibility.Collapsed;

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
        TxtTime.Text = TxtTimeMini.Text = Timecode(_sync.PositionTime);
        TxtDuration.Text = _sync.IsOpen ? "/ " + Timecode(_sync.Duration) : "/ --:--:--.---";
        TxtDurationMini.Text = TxtDuration.Text;

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
        ShowTrackLabels(_a, TxtFrameA, FrameBadgeA, PaneOsdA, PaneMasterA, NoFrameA, NoFrameHintA, TxtFrameAMini);
        ShowTrackLabels(_b, TxtFrameB, FrameBadgeB, PaneOsdB, PaneMasterB, NoFrameB, NoFrameHintB, TxtFrameBMini);
    }

    private void ShowTrackLabels(PlayerTrack track, TextBlock badgeText, UIElement badge,
        TextBlock osd, TextBlock role, UIElement noFrame, TextBlock noFrameHint, TextBlock miniText)
    {
        if (!track.IsOpen)
        {
            badgeText.Text = "";
            miniText.Text = "";
            osd.Text = "";
            role.Text = "";
            noFrame.Visibility = Visibility.Collapsed;
            return;
        }

        var frame = _sync.DisplayFrame(track);
        var approx = track.Media!.IsVariableFrameRate ? "≈" : "";

        badgeText.Text = frame is { } f
            ? $"{track.Letter} {approx}{f} / {Math.Max(track.FrameCount - 1, 0)}"
            : $"{track.Letter} —";
        badge.Opacity = frame is null ? 0.45 : 1;

        // В компактном подвале места меньше: номер кадра без общего числа.
        miniText.Text = frame is { } mini ? $"{track.Letter} {approx}{mini}" : $"{track.Letter} —";

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
