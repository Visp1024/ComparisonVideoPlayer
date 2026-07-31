using System.Globalization;
using System.IO;

namespace ComparisonPlayer.Export;

/// <summary>Как резать отрезок: с перекодированием или копией потока.</summary>
public enum ExportMode
{
    /// <summary>
    /// Перекодирование: границы ровно те, что выставлены ручками отрезка.
    /// </summary>
    Precise,

    /// <summary>
    /// Копия потока без перекодирования: мгновенно и без потери качества, но начало
    /// отъезжает к ближайшему ключевому кадру исходника.
    /// </summary>
    Copy
}

/// <summary>Что вырезаем.</summary>
/// <param name="SourcePath">Исходный файл — всегда он, а не прокси кэша.</param>
/// <param name="OutputPath">Куда пишем.</param>
/// <param name="Start">Начало отрезка от начала ролика.</param>
/// <param name="Duration">Длительность отрезка.</param>
/// <param name="Frames">Сколько кадров в отрезке: из них считается прогресс.</param>
/// <param name="Mode">Режим кодирования.</param>
/// <param name="SourceIsVfr">
/// У исходника переменная частота кадров: число кадров тогда оценка, и ограничивать
/// им вывод нельзя — резать приходится только по времени.
/// </param>
public readonly record struct ExportRequest(
    string SourcePath, string OutputPath, TimeSpan Start, TimeSpan Duration, long Frames, ExportMode Mode,
    bool SourceIsVfr = false);

/// <summary>
/// Вырезание отрезка трека в отдельный файл через ffmpeg CLI (задача #40).
/// </summary>
/// <remarks>
/// Режем всегда исходник, даже когда трек играет из прокси кэша: прокси — рабочая
/// копия с пониженным качеством и, возможно, частотой, и годится он для шага по
/// кадрам, а не для файла, который забирают из плеера. Границы отрезка при этом
/// переводятся из кадров в время — время у прокси и исходника общее.
/// </remarks>
public static class SegmentExporter
{
    /// <summary>Качество перекодирования: то же, что у прокси кэша.</summary>
    private const int Crf = 20;

    /// <summary>
    /// Собрать имя файла по умолчанию: имя ролика плюс границы отрезка. Занятое имя
    /// не перезаписываем — дописываем номер, чтобы исходник и прошлые куски уцелели.
    /// </summary>
    public static string SuggestPath(string sourcePath, TimeSpan start, TimeSpan end, ExportMode mode)
    {
        var dir = Path.GetDirectoryName(sourcePath) ?? "";
        var name = Path.GetFileNameWithoutExtension(sourcePath);

        // Копия потока ложится не во всякий контейнер (тот же HEVC в mp4 без правки
        // тегов), поэтому в быстром режиме контейнер остаётся исходным.
        var extension = mode == ExportMode.Precise ? ".mp4" : Path.GetExtension(sourcePath);
        if (extension.Length == 0) extension = ".mp4";

        var stem = $"{name}_{Stamp(start)}_{Stamp(end)}";
        var candidate = Path.Combine(dir, stem + extension);

        for (var i = 2; File.Exists(candidate); i++)
            candidate = Path.Combine(dir, $"{stem} ({i}){extension}");

        return candidate;
    }

    /// <summary>Метка времени для имени файла: часы-минуты-секунды без разделителей пути.</summary>
    private static string Stamp(TimeSpan time) =>
        $"{(int)time.TotalHours:00}-{time.Minutes:00}-{time.Seconds:00}";

    /// <summary>
    /// Вырезать отрезок. Недописанный файл после отказа или отмены удаляется:
    /// оборванный кусок выглядел бы готовым результатом.
    /// </summary>
    public static async Task RunAsync(ExportRequest request, Action<FFmpegProgress>? progress, CancellationToken ct)
    {
        try
        {
            await FFmpegRun.RunAsync(Args(request), Math.Max(request.Frames, 1), progress, ct);
        }
        catch (Exception)
        {
            SafeDelete(request.OutputPath);
            throw;
        }
    }

    /// <summary>
    /// Аргументы ffmpeg. <c>-ss</c> стоит до <c>-i</c>: так ffmpeg перематывает
    /// демуксером и декодирует только нужный кусок, а точность при перекодировании
    /// всё равно покадровая (он отбрасывает кадры до заданного времени сам).
    /// </summary>
    private static string Args(ExportRequest request)
    {
        var head =
            $"-hide_banner -nostdin -loglevel error -y " +
            $"-ss {Seconds(request.Start)} -i \"{request.SourcePath}\" -t {Seconds(request.Duration)} " +
            $"-map 0:v:0 -map 0:a:0? -sn -dn ";

        // Звук берём из исходника, если он там есть: «?» у дорожки означает
        // «если она есть» — немой ролик так и остаётся немым, а не отказом.
        // -frames:v закрывает хвост: -t ограничивает по времени, а кадров при дробной
        // длительности могло бы выйти на один больше. При VFR число кадров — оценка
        // по средней частоте, и обрезать им вывод опаснее, чем оставить как есть.
        var limit = request.SourceIsVfr || request.Frames <= 0 ? "" : $"-frames:v {request.Frames} ";

        var body = request.Mode == ExportMode.Precise
            ? limit +
              $"-c:v libx264 -preset veryfast -crf {Crf} -pix_fmt yuv420p " +
              $"-c:a aac -b:a 192k -movflags +faststart "
            // Копия начинается с ключевого кадра, и его метка времени отрицательна
            // относительно нового начала — make_zero сдвигает шкалу к нулю.
            : "-c copy -avoid_negative_ts make_zero ";

        return head + body + $"-progress pipe:1 -nostats \"{request.OutputPath}\"";
    }

    private static string Seconds(TimeSpan time) =>
        time.TotalSeconds.ToString("0.######", CultureInfo.InvariantCulture);

    private static void SafeDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception) { /* файл ещё занят снятым процессом: оставим как есть */ }
    }
}
