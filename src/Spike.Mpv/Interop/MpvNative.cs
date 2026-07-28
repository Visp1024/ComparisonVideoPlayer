using System.Runtime.InteropServices;
using System.Text;

namespace Spike.Mpv.Interop;

/// <summary>Сырые импорты libmpv (client.h, API 2.5). Строки — UTF-8 с нулевым байтом.</summary>
internal static class MpvNative
{
    private const string Dll = "libmpv-2.dll";
    private const CallingConvention Cdecl = CallingConvention.Cdecl;

    [DllImport(Dll, CallingConvention = Cdecl)] internal static extern IntPtr mpv_create();
    [DllImport(Dll, CallingConvention = Cdecl)] internal static extern int mpv_initialize(IntPtr ctx);
    [DllImport(Dll, CallingConvention = Cdecl)] internal static extern void mpv_terminate_destroy(IntPtr ctx);
    [DllImport(Dll, CallingConvention = Cdecl)] internal static extern void mpv_free(IntPtr data);
    [DllImport(Dll, CallingConvention = Cdecl)] internal static extern IntPtr mpv_error_string(int error);
    [DllImport(Dll, CallingConvention = Cdecl)] internal static extern IntPtr mpv_client_api_version();

    [DllImport(Dll, CallingConvention = Cdecl)] internal static extern int mpv_set_option_string(IntPtr ctx, byte[] name, byte[] data);
    [DllImport(Dll, CallingConvention = Cdecl)] internal static extern int mpv_set_property_string(IntPtr ctx, byte[] name, byte[] data);
    [DllImport(Dll, CallingConvention = Cdecl)] internal static extern int mpv_set_option(IntPtr ctx, byte[] name, int format, ref long data);

    [DllImport(Dll, CallingConvention = Cdecl)] internal static extern int mpv_command(IntPtr ctx, IntPtr[] args);
    [DllImport(Dll, CallingConvention = Cdecl)] internal static extern int mpv_command_async(IntPtr ctx, ulong replyUserdata, IntPtr[] args);

    [DllImport(Dll, CallingConvention = Cdecl, EntryPoint = "mpv_get_property")]
    internal static extern int mpv_get_property_double(IntPtr ctx, byte[] name, int format, out double data);
    [DllImport(Dll, CallingConvention = Cdecl, EntryPoint = "mpv_get_property")]
    internal static extern int mpv_get_property_long(IntPtr ctx, byte[] name, int format, out long data);
    [DllImport(Dll, CallingConvention = Cdecl, EntryPoint = "mpv_get_property")]
    internal static extern int mpv_get_property_ptr(IntPtr ctx, byte[] name, int format, out IntPtr data);

    [DllImport(Dll, CallingConvention = Cdecl)] internal static extern int mpv_observe_property(IntPtr ctx, ulong replyUserdata, byte[] name, int format);
    [DllImport(Dll, CallingConvention = Cdecl)] internal static extern IntPtr mpv_wait_event(IntPtr ctx, double timeout);
    [DllImport(Dll, CallingConvention = Cdecl)] internal static extern void mpv_wakeup(IntPtr ctx);
    [DllImport(Dll, CallingConvention = Cdecl)] internal static extern int mpv_request_log_messages(IntPtr ctx, byte[] minLevel);

    internal static byte[] U8(string s) => Encoding.UTF8.GetBytes(s + "\0");

    internal static string? Utf8(IntPtr p) => p == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(p);

    internal static string ErrorText(int code) => Utf8(mpv_error_string(code)) ?? $"код {code}";
}

internal enum MpvFormat
{
    None = 0,
    String = 1,
    OsdString = 2,
    Flag = 3,
    Int64 = 4,
    Double = 5,
    Node = 6
}

internal enum MpvEventId
{
    None = 0,
    Shutdown = 1,
    LogMessage = 2,
    GetPropertyReply = 3,
    SetPropertyReply = 4,
    CommandReply = 5,
    StartFile = 6,
    EndFile = 7,
    FileLoaded = 8,
    Idle = 11,
    Tick = 14,
    ClientMessage = 16,
    VideoReconfig = 17,
    AudioReconfig = 18,
    Seek = 20,
    PlaybackRestart = 21,
    PropertyChange = 22,
    QueueOverflow = 24,
    Hook = 25
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvEvent
{
    public MpvEventId EventId;
    public int Error;
    public ulong ReplyUserdata;
    public IntPtr Data;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvEventProperty
{
    public IntPtr Name;
    public MpvFormat Format;
    public IntPtr Data;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvEventLogMessage
{
    public IntPtr Prefix;
    public IntPtr Level;
    public IntPtr Text;
    public int LogLevel;
}
