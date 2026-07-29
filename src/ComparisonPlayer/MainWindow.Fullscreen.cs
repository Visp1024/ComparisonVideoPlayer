using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace ComparisonPlayer;

/// <summary>
/// Полноэкранный режим (задача #28): на экране остаётся только кадр, а транспорт
/// выезжает снизу, когда двинули мышью, и уезжает по бездействию.
/// </summary>
/// <remarks>
/// Полоса живёт в <c>Popup</c> — отдельном окне поверх всех: кадр рисует FlyleafHost
/// в собственных окнах, и накладка главного окна оказалась бы под картинкой.
/// По той же причине движение мыши ловим не только у окна, но и у накладок обоих
/// треков: над кадром события мыши достаются им, а не главному окну.
///
/// Состояние живёт в течение сеанса и в настройках не хранится: запуск сразу во весь
/// экран прятал бы от человека всё управление разом.
/// </remarks>
public partial class MainWindow
{
    /// <summary>Через сколько бездействия мыши полоса уезжает.</summary>
    private static readonly TimeSpan FsIdle = TimeSpan.FromSeconds(2.5);

    /// <summary>Сколько полоса едет вверх и вниз.</summary>
    private static readonly Duration FsSlideTime = new(TimeSpan.FromMilliseconds(180));

    /// <summary>
    /// Насколько должна сместиться мышь, чтобы это считалось движением. Дрожание в
    /// один пиксель и события, приходящие от самой выехавшей полосы, полосу бы
    /// показывали заново и не давали ей уехать никогда.
    /// </summary>
    private const double FsMouseThreshold = 2;

    private bool _fullscreen;
    private bool _fsBarShown;
    private Point _fsLastMouse;

    private readonly DispatcherTimer _fsIdleTimer = new() { Interval = FsIdle };

    private void FullScreen_Click(object sender, RoutedEventArgs e) => ToggleFullScreen();

    private void InitFullScreen()
    {
        _fsIdleTimer.Tick += (_, _) => OnFsIdle();

        FsProgress.ScrubStarted += (_, _) =>
        {
            StopShuttle();
            _scrubbing = true;
        };
        FsProgress.ScrubMoved += (_, frame) => TimelineScrub(frame);
        FsProgress.ScrubEnded += (_, frame) => TimelineScrubEnd(frame);

        // Двойной клик по кадру — привычный вход и выход у любого плеера. Своими
        // силами: у FlyleafHost та же реакция отключена (она разворачивала бы
        // на весь экран одно окно вывода, без нашего интерфейса). Ловим классовым
        // обработчиком: кадр выводят чужие окна, и до накладки событие доходит не
        // всегда — FlyleafHost разбирает нажатия сам и помечает их обработанными.
        EventManager.RegisterClassHandler(typeof(Window), Mouse.PreviewMouseDownEvent,
            new MouseButtonEventHandler(OnFrameClick), true);

        // Движение мыши ловим во всех окнах приложения и даже помеченное обработанным —
        // тем же приёмом, что клавиши и колесо в конструкторе окна: над кадром события
        // достаются окнам вывода FlyleafHost, а не главному окну.
        EventManager.RegisterClassHandler(typeof(Window), Mouse.PreviewMouseMoveEvent,
            new MouseEventHandler(OnFsMouseMove), true);

        // Полоса живёт в Popup, а его корень — не Window, и классовый обработчик до
        // него не доходит: движение по самой полосе подписываем отдельно.
        FsBar.MouseMove += OnFsMouseMove;

        // Полоса шириной в окно и прижата к его низу: у Popup своё окно, и за
        // размером главного оно само не следит.
        SizeChanged += (_, _) => PlaceFsBar();
        FsBar.SizeChanged += (_, _) => PlaceFsBar();
    }

    /// <summary>
    /// Двойной клик по кадру. Кадр — это всё, что не главное окно: окна вывода
    /// FlyleafHost и их накладки. Клики по кнопкам, таймлайну и полосе приходят
    /// от самого окна (и от Popup, который окном не является) — их не трогаем.
    /// </summary>
    private void OnFrameClick(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(sender, this)) return;
        if (e.ChangedButton != MouseButton.Left || e.ClickCount != 2) return;

        // Одно нажатие доходит до обработчика из нескольких окон вывода: метка
        // времени у него общая, по ней и отсекаем повтор (как для клавиш).
        if (e.Timestamp == _lastFrameClickStamp) return;

        _lastFrameClickStamp = e.Timestamp;
        ToggleFullScreen();
    }

    /// <summary>Метка времени последнего обработанного двойного клика по кадру.</summary>
    private int _lastFrameClickStamp = -1;

    private void ToggleFullScreen()
    {
        if (_fullscreen) LeaveFullScreen();
        else EnterFullScreenMode();
    }

    private void EnterFullScreenMode()
    {
        if (_fullscreen) return;

        _fullscreen = true;
        EnterFullScreen();

        ApplyFullScreen();
        ShowFsBar();
        Status("во весь экран: двиньте мышью — выедет полоса; выход — F11 или Esc");
    }

    private void LeaveFullScreen()
    {
        if (!_fullscreen) return;

        _fullscreen = false;
        HideFsBar(animate: false);
        _fsIdleTimer.Stop();
        ExitFullScreen();

        ApplyFullScreen();
        Status("обычный вид (F11)");
    }

    /// <summary>
    /// Показать или спрятать всё, кроме кадра. Таймлайн, транспорт и подвал прячет
    /// <see cref="ApplyCompact"/> — он и так решает, что из них видно.
    /// </summary>
    private void ApplyFullScreen()
    {
        var chrome = _fullscreen ? Visibility.Collapsed : Visibility.Visible;

        Bar.Visibility = chrome;
        Toolbar.Visibility = chrome;

        // Метка трека поверх кадра (имя файла, таймкод, роль) — тоже интерфейс:
        // «остаётся только кадр» значит и без неё. Кто где, видно по выехавшей полосе.
        PaneLabelA.Visibility = chrome;
        PaneLabelB.Visibility = chrome;

        ApplyCompact();
        UpdateState();
        ApplySideVisibility();
        ShowFsCursor(true);
    }

    // ---------- выезжающая полоса ----------

    private void OnFsMouseMove(object sender, MouseEventArgs e)
    {
        if (!_fullscreen) return;

        // Координаты берём в окне того элемента, где случилось событие: накладка
        // трека — чужое окно, и пересчитать её точку в наше нельзя. Для «двинули
        // ли мышью» этого довольно.
        var point = e.GetPosition(null);
        var moved = Math.Abs(point.X - _fsLastMouse.X) > FsMouseThreshold ||
                    Math.Abs(point.Y - _fsLastMouse.Y) > FsMouseThreshold;

        _fsLastMouse = point;
        if (!moved) return;

        ShowFsBar();
    }

    /// <summary>Полоса уезжает по бездействию — но не из-под курсора, которым ею пользуются.</summary>
    private void OnFsIdle()
    {
        if (!_fullscreen)
        {
            _fsIdleTimer.Stop();
            return;
        }

        if (FsBar.IsMouseOver) return;

        HideFsBar(animate: true);
    }

    private void ShowFsBar()
    {
        _fsIdleTimer.Stop();
        _fsIdleTimer.Start();
        ShowFsCursor(true);

        if (_fsBarShown) return;

        _fsBarShown = true;
        FsPopup.IsOpen = true;
        PlaceFsBar();

        Slide(from: FsBarHeight, to: 0, onDone: null);
    }

    private void HideFsBar(bool animate)
    {
        if (!_fsBarShown)
        {
            FsPopup.IsOpen = false;
            return;
        }

        _fsBarShown = false;

        if (!animate)
        {
            FsSlide.BeginAnimation(TranslateTransform.YProperty, null);
            FsSlide.Y = 0;
            FsPopup.IsOpen = false;
            return;
        }

        // Курсор прячем вместе с полосой: в полноэкранном виде он такая же
        // деталь интерфейса, как она.
        ShowFsCursor(false);
        Slide(from: 0, to: FsBarHeight, onDone: () =>
        {
            if (!_fsBarShown) FsPopup.IsOpen = false;
        });
    }

    private void Slide(double from, double to, Action? onDone)
    {
        var animation = new DoubleAnimation(from, to, FsSlideTime)
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };

        if (onDone is not null) animation.Completed += (_, _) => onDone();

        FsSlide.BeginAnimation(TranslateTransform.YProperty, animation);
    }

    /// <summary>Высота полосы; до первой отрисовки её ещё нет, и ход анимации берём с запасом.</summary>
    private double FsBarHeight => FsBar.ActualHeight > 0 ? FsBar.ActualHeight : 88;

    private void PlaceFsBar()
    {
        if (!FsPopup.IsOpen) return;

        FsBar.Width = Root.ActualWidth;
        FsPopup.HorizontalOffset = 0;
        FsPopup.VerticalOffset = Math.Max(Root.ActualHeight - FsBarHeight, 0);
    }

    /// <summary>Курсор спрятан нашим <see cref="ShowCursor"/>: счётчик надо возвращать ровно раз.</summary>
    private bool _cursorHidden;

    /// <summary>
    /// Курсор в полноэкранном виде уходит вместе с полосой: пока её нет, на экране
    /// только кадр. Одних свойств WPF мало — кадр выводят окна FlyleafHost, и курсор
    /// над ними принадлежит им, поэтому прячем его ещё и системно, на всё приложение.
    /// </summary>
    private void ShowFsCursor(bool visible)
    {
        var hide = !visible && _fullscreen;
        var cursor = hide ? Cursors.None : Cursors.Arrow;

        Cursor = cursor;
        OverlayA.Cursor = cursor;
        OverlayB.Cursor = cursor;
        FsBar.Cursor = Cursors.Arrow;

        if (hide == _cursorHidden) return;

        _cursorHidden = hide;
        ShowCursor(!hide);
    }

    /// <summary>
    /// Системный счётчик видимости курсора: он общий на все окна приложения, поэтому
    /// каждое скрытие обязано быть возвращено — иначе курсор пропадёт и после выхода.
    /// </summary>
    [DllImport("user32.dll")]
    private static extern int ShowCursor([MarshalAs(UnmanagedType.Bool)] bool show);
}
