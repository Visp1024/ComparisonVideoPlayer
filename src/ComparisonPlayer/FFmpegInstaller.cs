using System.IO;
using System.IO.Compression;
using System.Net.Http;

namespace ComparisonPlayer;

/// <summary>Что происходит прямо сейчас — для полосы и подписи в окне загрузки.</summary>
public enum FFmpegInstallStage
{
    Download,
    Extract
}

/// <summary>
/// Ход установки. <see cref="Total"/> равен нулю, пока размер неизвестен (сервер не прислал
/// Content-Length) — окно в этом случае показывает неопределённую полосу.
/// </summary>
public readonly record struct FFmpegInstallProgress(FFmpegInstallStage Stage, long Done, long Total);

/// <summary>
/// Скачивает нативные библиотеки FFmpeg и кладёт их рядом с программой. Нужен на чистой
/// машине: без библиотек FlyleafLib не поднимает движок и не открывается ни один файл,
/// а раньше пользователю оставалось только идти искать сборку руками.
/// </summary>
public static class FFmpegInstaller
{
    /// <summary>
    /// Сборка BtbN: тот же комплект (n7.1, win64, gpl, shared), под который собран
    /// FlyleafLib 3.10.4 и который кладёт в поставку tools/publish.ps1. Тег latest у этого
    /// релиза постоянный — файл под ним обновляется, ссылка не протухает.
    /// </summary>
    public const string DownloadUrl =
        "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-n7.1-latest-win64-gpl-shared-7.1.zip";

    /// <summary>Примерный размер архива — показываем до начала загрузки, чтобы решение было осознанным.</summary>
    public const int ApproxDownloadMb = 68;

    /// <summary>
    /// Куда положим библиотеки. «Рядом с программой» — подкаталог FFmpeg возле exe: там их
    /// ищет <see cref="AppEnv"/> и туда же их кладёт публикационный билд. Если каталог не
    /// пишется (установка в Program Files под обычным пользователем) — уходим в данные
    /// приложения: это по-прежнему каталог, который AppEnv примет через UseFFmpegDir.
    /// </summary>
    public static string TargetDir =>
        IsWritable(AppContext.BaseDirectory)
            ? Path.Combine(AppContext.BaseDirectory, "FFmpeg")
            : Path.Combine(AppEnv.DataDir, "FFmpeg");

    /// <summary>
    /// Скачивает архив, распаковывает из него библиотеки и ffmpeg.exe в <see cref="TargetDir"/>
    /// и сразу переключает на этот каталог <see cref="AppEnv"/>. Возвращает каталог установки.
    /// Бросает исключение, если что-то не вышло: разбирать его — дело вызывающего окна.
    /// </summary>
    public static async Task<string> InstallAsync(
        IProgress<FFmpegInstallProgress>? progress,
        CancellationToken cancel)
    {
        var target = TargetDir;
        var targetExisted = Directory.Exists(target);
        Directory.CreateDirectory(target);

        // Архив качаем во временный файл, а не в память: 68 МБ в байтовом массиве ради
        // одной распаковки — лишний расход, да и ZipFile читает с диска потоково.
        var temp = Path.Combine(Path.GetTempPath(), $"cvp-ffmpeg-{Environment.ProcessId}.zip");

        try
        {
            await DownloadAsync(temp, progress, cancel);
            Extract(temp, target, progress, cancel);
        }
        catch (Exception)
        {
            // Отмена на первых секундах не должна оставлять рядом с exe пустой каталог
            // FFmpeg: он ничего не даёт, а в поиске каталогов выглядит как найденный.
            if (!targetExisted) TryRemoveEmpty(target);
            throw;
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch (IOException) { }                       // временный файл переживёт перезагрузку — не беда
        }

        if (!AppEnv.FFmpegLooksUsableIn(target))
            throw new InvalidOperationException(
                $"в архиве не нашлось библиотек avcodec — каталог {target} остался непригодным");

        AppEnv.UseFFmpegDir(target);
        return target;
    }

    private static async Task DownloadAsync(
        string temp,
        IProgress<FFmpegInstallProgress>? progress,
        CancellationToken cancel)
    {
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ComparisonVideoPlayer");

        // ResponseHeadersRead обязателен: иначе HttpClient сначала выкачает весь архив в
        // память и полоса прогресса прыгнет с нуля до конца одним скачком.
        using var response = await http.GetAsync(
            DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancel);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? 0;
        progress?.Report(new FFmpegInstallProgress(FFmpegInstallStage.Download, 0, total));

        await using var source = await response.Content.ReadAsStreamAsync(cancel);
        await using var file = new FileStream(
            temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);

        var buffer = new byte[1 << 16];
        long done = 0;
        var lastReport = 0L;
        int read;
        while ((read = await source.ReadAsync(buffer, cancel)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read), cancel);
            done += read;

            // Отчитываемся не на каждый блок: 68 МБ по 64 КБ — это тысяча с лишним
            // обновлений окна, полосе хватает шага в четверть мегабайта.
            if (done - lastReport < 256 * 1024 && done != total) continue;
            lastReport = done;
            progress?.Report(new FFmpegInstallProgress(FFmpegInstallStage.Download, done, total));
        }
    }

    /// <summary>
    /// Достаёт из архива только содержимое его каталога bin — библиотеки и утилиты; всё
    /// прочее (заголовки, лицензии, doc) в работе не участвует. ffplay пропускаем вслед за
    /// tools/publish.ps1: он тянет SDL и никем не вызывается.
    /// </summary>
    private static void Extract(
        string archive,
        string target,
        IProgress<FFmpegInstallProgress>? progress,
        CancellationToken cancel)
    {
        using var zip = ZipFile.OpenRead(archive);

        var wanted = zip.Entries
            .Where(e => e.FullName.Contains("/bin/", StringComparison.OrdinalIgnoreCase))
            .Where(e => Path.GetExtension(e.Name) is ".dll" or ".exe")
            .Where(e => !e.Name.Equals("ffplay.exe", StringComparison.OrdinalIgnoreCase))
            .ToList();

        progress?.Report(new FFmpegInstallProgress(FFmpegInstallStage.Extract, 0, wanted.Count));

        for (var i = 0; i < wanted.Count; i++)
        {
            cancel.ThrowIfCancellationRequested();
            // Имя берём без пути из архива — раскладывать bin/ внутрь FFmpeg/ незачем,
            // AppEnv ждёт библиотеки прямо в каталоге. Заодно это отсекает путь наружу.
            wanted[i].ExtractToFile(Path.Combine(target, wanted[i].Name), overwrite: true);
            progress?.Report(new FFmpegInstallProgress(FFmpegInstallStage.Extract, i + 1, wanted.Count));
        }
    }

    private static void TryRemoveEmpty(string dir)
    {
        try
        {
            if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                Directory.Delete(dir);
        }
        catch (Exception)
        {
            // Пустой каталог никому не мешает — молча оставляем.
        }
    }

    /// <summary>
    /// Можно ли писать в каталог. Проверяем делом, а не правами: разбор ACL длиннее и всё
    /// равно врёт на сетевых дисках и при виртуализации каталогов.
    /// </summary>
    private static bool IsWritable(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, $".cvp-write-{Environment.ProcessId}");
            using (File.Create(probe, 1, FileOptions.DeleteOnClose)) { }
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
