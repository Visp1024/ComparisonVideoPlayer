using System.IO;

namespace ComparisonPlayer;

/// <summary>
/// Внешние пути приложения. FlyleafLib работает поверх нативных библиотек FFmpeg —
/// их каталог не входит в сборку, поэтому ищется по переменной окружения,
/// рядом с exe и в известных местах машины разработки.
/// </summary>
public static class AppEnv
{
    /// <summary>Каталог нативных библиотек FFmpeg (avcodec, avformat и прочие).</summary>
    public static string FFmpegDir { get; } =
        Environment.GetEnvironmentVariable("COMPARISONPLAYER_FFMPEG_DIR")
        ?? Environment.GetEnvironmentVariable("SPIKE_FFMPEG_DIR")
        ?? Probe(
            Path.Combine(AppContext.BaseDirectory, "FFmpeg"),
            @"D:\PROJECTS\_tools\ffmpeg-n7.1-latest-win64-gpl-shared-7.1\bin",
            @"C:\ffmpeg\bin")
        ?? "";

    /// <summary>Каталог пользовательских данных приложения: настройки, журнал движка.</summary>
    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ComparisonVideoPlayer");

    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string EngineLogFile => Path.Combine(DataDir, "flyleaf.log");

    private static string? Probe(params string[] candidates)
        => candidates.FirstOrDefault(Directory.Exists);
}
