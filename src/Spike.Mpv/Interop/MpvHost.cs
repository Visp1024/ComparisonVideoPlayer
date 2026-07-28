using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Spike.Mpv.Interop;

/// <summary>
/// Дочернее Win32-окно внутри WPF-разметки: libmpv рендерит прямо в его HWND (опция wid).
/// Своего рендера у спайка нет — этого достаточно, чтобы проверить встраивание.
/// </summary>
public sealed class MpvHost : HwndHost
{
    private const int WsChild = 0x40000000;
    private const int WsVisible = 0x10000000;
    private const int WsClipChildren = 0x02000000;

    /// <summary>HWND, который отдаётся libmpv. Доступен после того, как контрол загружен.</summary>
    public IntPtr VideoHandle { get; private set; }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        VideoHandle = CreateWindowEx(0, "static", "", WsChild | WsVisible | WsClipChildren,
            0, 0, 320, 180, hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        if (VideoHandle == IntPtr.Zero)
            throw new InvalidOperationException($"CreateWindowEx не создал окно: {Marshal.GetLastWin32Error()}");

        return new HandleRef(this, VideoHandle);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        DestroyWindow(hwnd.Handle);
        VideoHandle = IntPtr.Zero;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hwnd);
}
