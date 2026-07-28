using System.IO;

namespace Spike.Mpv;

/// <summary>
/// Пути к внешним зависимостям спайка: libmpv (нативная библиотека плеера), ffmpeg.exe
/// (эталонные кадры для проверки точности seek) и тестовые ролики.
/// Всё переопределяется переменными окружения, чтобы спайк запускался и на другой машине.
/// Значения по умолчанию совпадают со спайком FlyleafLib — замеры должны быть сравнимы.
/// </summary>
public static class SpikeEnv
{
    /// <summary>Папка с libmpv-2.dll (пакет mpv-dev-x86_64 от shinchiro).</summary>
    public static string MpvDir { get; } =
        Environment.GetEnvironmentVariable("SPIKE_MPV_DIR")
        ?? Probe(@"D:\PROJECTS\_tools\mpv-dev")
        ?? "";

    public static string MpvDll => Path.Combine(MpvDir, "libmpv-2.dll");

    public static string FFmpegDir { get; } =
        Environment.GetEnvironmentVariable("SPIKE_FFMPEG_DIR")
        ?? Probe(
            @"D:\PROJECTS\_tools\ffmpeg-n7.1-latest-win64-gpl-shared-7.1\bin",
            @"C:\ffmpeg\bin")
        ?? "";

    public static string FFmpegExe => Path.Combine(FFmpegDir, "ffmpeg.exe");

    public static string MediaDir { get; } =
        Environment.GetEnvironmentVariable("SPIKE_MEDIA_DIR")
        ?? Probe(@"D:\PROJECTS\_tools\testmedia")
        ?? "";

    public static string OutDir { get; } =
        Environment.GetEnvironmentVariable("SPIKE_MPV_OUT_DIR")
        ?? Path.Combine(Path.GetTempPath(), "spike-mpv");

    private static string? Probe(params string[] candidates)
        => candidates.FirstOrDefault(Directory.Exists);
}
