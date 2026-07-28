using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;

namespace ComparisonPlayer.Chrome;

/// <summary>
/// Окно со своей титульной полосой (задача #21). Родная шапка Windows светлая и остаётся
/// единственным светлым пятном поверх почти чёрного кадра, поэтому её рисуем сами.
///
/// Полосу рисует <see cref="TitleBar"/>, а это окно отвечает за рамку: <see cref="WindowChrome"/>
/// с <c>CaptionHeight</c> высотой полосы оставляет системе перетаскивание, двойной клик,
/// прилипание к краям и меню по правой кнопке — свой код всего этого повторял бы Windows хуже.
/// Кнопкам полосы нужен <c>WindowChrome.IsHitTestVisibleInChrome</c>, иначе щелчки по ним
/// достанутся заголовку.
/// </summary>
public class AppWindow : Window
{
    /// <summary>Высота титульной полосы; она же — высота области заголовка для Windows.</summary>
    public const double TitleBarHeight = 30;

    public AppWindow()
    {
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = TitleBarHeight,
            ResizeBorderThickness = new Thickness(6),
            CornerRadius = new CornerRadius(0),
            // Рамку и тень рисует DWM: с ненулевым GlassFrameThickness поверх нашей полосы
            // проступает светлая линия системного фрейма.
            GlassFrameThickness = new Thickness(0),
            UseAeroCaptionButtons = false
        });

        CommandBindings.Add(new CommandBinding(SystemCommands.MinimizeWindowCommand,
            (_, _) => SystemCommands.MinimizeWindow(this)));
        CommandBindings.Add(new CommandBinding(SystemCommands.MaximizeWindowCommand,
            (_, _) => SystemCommands.MaximizeWindow(this)));
        CommandBindings.Add(new CommandBinding(SystemCommands.RestoreWindowCommand,
            (_, _) => SystemCommands.RestoreWindow(this)));
        CommandBindings.Add(new CommandBinding(SystemCommands.CloseWindowCommand,
            (_, _) => Close()));
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        FitMaximizedToWorkArea();
    }

    /// <summary>
    /// Развёрнутое окно Windows делает на рамку изменения размера больше рабочей области —
    /// у обычного окна эта рамка нерисуемая, а у нашего в неё попадает содержимое: верх
    /// титульной полосы уезжает за край экрана. Отступ считаем по факту, разностью
    /// прямоугольников: в пикселях он зависит от масштаба экрана, и зашивать «8» нельзя.
    /// </summary>
    private void FitMaximizedToWorkArea()
    {
        if (WindowState != WindowState.Maximized)
        {
            BorderThickness = new Thickness(0);
            return;
        }

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero) return;

        var info = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return;
        if (!GetWindowRect(hwnd, out var window)) return;

        var dpi = VisualTreeHelper.GetDpi(this);
        BorderThickness = new Thickness(
            Inset(info.rcWork.Left - window.Left, dpi.DpiScaleX),
            Inset(info.rcWork.Top - window.Top, dpi.DpiScaleY),
            Inset(window.Right - info.rcWork.Right, dpi.DpiScaleX),
            Inset(window.Bottom - info.rcWork.Bottom, dpi.DpiScaleY));

        static double Inset(int pixels, double scale) => pixels > 0 ? pixels / scale : 0;
    }

    // ---------- interop ----------

    private const int MonitorDefaultToNearest = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect32
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int cbSize;
        public Rect32 rcMonitor;
        public Rect32 rcWork;
        public int dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect32 rect);
}
