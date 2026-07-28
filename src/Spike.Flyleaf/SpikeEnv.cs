using System.IO;

namespace Spike.Flyleaf;

/// <summary>
/// Пути к внешним зависимостям спайка: нативные библиотеки FFmpeg (нужны FlyleafLib),
/// ffmpeg.exe (эталонные кадры для проверки точности seek) и тестовые ролики.
/// Всё переопределяется переменными окружения, чтобы спайк запускался и на другой машине.
/// </summary>
public static class SpikeEnv
{
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
        Environment.GetEnvironmentVariable("SPIKE_OUT_DIR")
        ?? Path.Combine(Path.GetTempPath(), "spike-flyleaf");

    private static string? Probe(params string[] candidates)
        => candidates.FirstOrDefault(Directory.Exists);
}
