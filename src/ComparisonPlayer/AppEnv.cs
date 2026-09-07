using System.IO;

namespace ComparisonPlayer;

/// <summary>
/// Внешние пути приложения. FlyleafLib работает поверх нативных библиотек FFmpeg —
/// их каталог не входит в сборку, поэтому ищется по переменной окружения,
/// рядом с exe и в известных местах машины разработки.
/// </summary>
public static class AppEnv
{
    /// <summary>
    /// Где ищем библиотеки, в порядке предпочтения: явное указание переменной окружения,
    /// подкаталог рядом с программой (туда кладёт комплект загрузчик), каталог репозитория
    /// для запуска из bin/Debug и общее место на машине.
    /// </summary>
    private static string?[] Candidates() =>
    [
        Environment.GetEnvironmentVariable("COMPARISONPLAYER_FFMPEG_DIR"),
        Environment.GetEnvironmentVariable("SPIKE_FFMPEG_DIR"),
        Path.Combine(AppContext.BaseDirectory, "FFmpeg"),
        RepoFFmpegDir(),
        @"C:\ffmpeg\bin"
    ];

    private static string _ffmpegDir = Probe(Candidates());

    /// <summary>Каталог нативных библиотек FFmpeg (avcodec, avformat и прочие).</summary>
    public static string FFmpegDir => _ffmpegDir;

    /// <summary>
    /// Переключает приложение на другой каталог библиотек — им пользуется загрузчик
    /// (<see cref="FFmpegInstaller"/>) сразу после установки, чтобы движок поднялся
    /// в этом же запуске, без перезапуска программы.
    /// </summary>
    public static void UseFFmpegDir(string dir)
    {
        _ffmpegDir = dir;
        _ffmpegExe = ResolveFFmpegExe(dir);
    }

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
    public static string FFmpegExe => _ffmpegExe;

    private static string _ffmpegExe = ResolveFFmpegExe(_ffmpegDir);

    private static string ResolveFFmpegExe(string dir)
        => dir.Length > 0 && File.Exists(Path.Combine(dir, "ffmpeg.exe"))
            ? Path.Combine(dir, "ffmpeg.exe")
            : "ffmpeg";

    /// <summary>
    /// Похож ли найденный каталог на рабочий комплект библиотек. Нужен, чтобы не сваливать
    /// на FFmpeg отказ движка по другой причине: искать несуществующую проблему дороже,
    /// чем прочитать настоящее сообщение об ошибке.
    /// </summary>
    public static bool FFmpegLooksUsable => FFmpegLooksUsableIn(_ffmpegDir);

    /// <summary>Тот же признак для произвольного каталога: им проверяется свежая установка.</summary>
    public static bool FFmpegLooksUsableIn(string dir)
    {
        try
        {
            return dir.Length > 0 && File.Exists(Path.Combine(dir, RequiredAvcodec));
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Библиотека avcodec той версии, под которую собран плеер. Проверяем именно её, а не
    /// «хоть какой-нибудь avcodec»: FlyleafLib прибит к своим биндингам FFmpeg (сейчас
    /// Flyleaf.FFmpeg.Bindings 9.0.0), и комплект другой ветки движок не поднимет. По маске
    /// с любым номером каталог со старым комплектом выглядел годным, окно установки не
    /// показывалось, а запуск падал на Engine.Start без единой подсказки, что делать.
    /// Меняется вместе с версией FlyleafLib — вслед за <see cref="FFmpegInstaller.DownloadUrl"/>.
    /// </summary>
    public const string RequiredAvcodec = "avcodec-63.dll";

    /// <summary>
    /// В каталоге лежит комплект FFmpeg другой версии: библиотеки есть, но не те. От «нет
    /// вовсе» это стоит отличать ради разговора с человеком: «FFmpeg не найден» про полный
    /// каталог выглядит как ошибка программы, а не как повод обновить комплект.
    /// </summary>
    public static bool FFmpegWrongVersionIn(string dir)
    {
        try
        {
            return dir.Length > 0
                && !FFmpegLooksUsableIn(dir)
                && Directory.EnumerateFiles(dir, "avcodec-*.dll").Any();
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Найденный комплект — не той версии (см. <see cref="FFmpegWrongVersionIn"/>).</summary>
    public static bool FFmpegWrongVersion => FFmpegWrongVersionIn(_ffmpegDir);

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

    /// <summary>
    /// Первый каталог с годным комплектом. Годного нет — отдаём первый существующий: по нему
    /// видно, что человек уже указал переменной окружения или положил рядом с программой, и
    /// именно про этот каталог пойдёт разговор в окне установки. Пусто — не нашли ничего.
    /// </summary>
    private static string Probe(string?[] candidates)
    {
        var existing = candidates
            .Where(c => !string.IsNullOrEmpty(c) && Directory.Exists(c))
            .Select(c => c!)
            .ToList();

        return existing.FirstOrDefault(FFmpegLooksUsableIn) ?? existing.FirstOrDefault() ?? "";
    }
}
