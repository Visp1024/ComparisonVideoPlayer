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

    /// <summary>
    /// Похож ли найденный каталог на рабочий комплект библиотек. Нужен, чтобы не сваливать
    /// на FFmpeg отказ движка по другой причине: искать несуществующую проблему дороже,
    /// чем прочитать настоящее сообщение об ошибке.
    /// </summary>
    public static bool FFmpegLooksUsable
    {
        get
        {
            try
            {
                return FFmpegDir.Length > 0
                    && Directory.EnumerateFiles(FFmpegDir, "avcodec-*.dll").Any();
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Каталог пользовательских данных приложения: настройки, журнал движка.
    /// Имя папки осталось прежним после переименования продукта в CVP (задача #26):
    /// новое имя увело бы уже накопленные настройки, сессию и кэш кадров.
    /// </summary>
    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ComparisonVideoPlayer");

    /// <summary>Каталог дискового кэша кадров: по папке на ролик, ключ — имя папки.</summary>
    public static string CacheDir { get; } = Path.Combine(DataDir, "cache");

    public static string SettingsFile => Path.Combine(DataDir, "settings.json");

    /// <summary>Последняя сессия: файлы треков, сдвиг, отрезки и позиция (фаза 5).</summary>
    public static string SessionFile => Path.Combine(DataDir, "session.json");

    /// <summary>
    /// Журнал движка. Файл держится открытым всё время работы, поэтому имя своё у каждого
    /// процесса: с общим именем вторая копия плеера не запускалась вовсе — Engine.Start
    /// падал на «файл занят другим процессом». Старые журналы подчищает <see cref="CleanupEngineLogs"/>.
    /// </summary>
    public static string EngineLogFile { get; } =
        Path.Combine(DataDir, $"flyleaf-{Environment.ProcessId}.log");

    /// <summary>Сколько журналов прошлых запусков оставляем: хвост нужен для разбора жалоб, но короткий.</summary>
    private const int KeepEngineLogs = 5;

    /// <summary>
    /// Удаляет журналы прошлых запусков сверх <see cref="KeepEngineLogs"/> свежих: имя с номером
    /// процесса иначе копило бы по файлу на запуск. Журнал живой копии плеера заперт ею же —
    /// удаление такого падает с IOException, и файл остаётся на месте.
    /// </summary>
    public static void CleanupEngineLogs()
    {
        try
        {
            var stale = new DirectoryInfo(DataDir)
                .EnumerateFiles("flyleaf*.log")
                .Where(f => f.FullName != EngineLogFile)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Skip(KeepEngineLogs);

            foreach (var file in stale)
            {
                try { file.Delete(); }
                catch (IOException) { }                  // занят живой копией плеера
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (Exception)
        {
            // Уборка журналов — гигиена, а не работа приложения: молча переживаем отказ.
        }
    }

    /// <summary>Запомненные замеры скорости шага назад, чтобы не мерить один файл дважды.</summary>
    public static string ProbeFile => Path.Combine(DataDir, "probes.json");

    private static string? Probe(params string?[] candidates)
        => candidates.FirstOrDefault(c => c is not null && Directory.Exists(c));
}
