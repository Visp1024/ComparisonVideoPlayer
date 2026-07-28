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
            RepoFFmpegDir(),
            @"C:\ffmpeg\bin")
        ?? "";

    /// <summary>
    /// Каталог FFmpeg внутри репозитория (tools/ffmpeg/bin) — им пользуется запуск из
    /// bin/Debug при разработке. Ищем вверх от каталога сборки, потому что глубина
    /// bin/&lt;конфигурация&gt;/&lt;tfm&gt;/&lt;rid&gt; относительно корня меняется.
    /// </summary>
    private static string? RepoFFmpegDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "tools", "ffmpeg", "bin");
            if (Directory.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>
    /// Исполняемый файл ffmpeg для сборки кэша кадров. Рядом с нативными библиотеками
    /// он лежит в тех же сборках FFmpeg; если каталог не найден — надеемся на PATH.
    /// </summary>
    public static string FFmpegExe { get; } =
        FFmpegDir.Length > 0 && File.Exists(Path.Combine(FFmpegDir, "ffmpeg.exe"))
            ? Path.Combine(FFmpegDir, "ffmpeg.exe")
            : "ffmpeg";

    /// <summary>Каталог пользовательских данных приложения: настройки, журнал движка.</summary>
    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ComparisonVideoPlayer");

    /// <summary>Каталог дискового кэша кадров: по папке на ролик, ключ — имя папки.</summary>
    public static string CacheDir { get; } = Path.Combine(DataDir, "cache");

    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string EngineLogFile => Path.Combine(DataDir, "flyleaf.log");

    /// <summary>Запомненные замеры скорости шага назад, чтобы не мерить один файл дважды.</summary>
    public static string ProbeFile => Path.Combine(DataDir, "probes.json");

    private static string? Probe(params string?[] candidates)
        => candidates.FirstOrDefault(c => c is not null && Directory.Exists(c));
}
