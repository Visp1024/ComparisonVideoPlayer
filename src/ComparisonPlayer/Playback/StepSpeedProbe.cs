using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace ComparisonPlayer.Playback;

/// <summary>
/// Замер реального шага назад — на нём строится автоматическое решение о кэше
/// (PLAN.md §4.1). Сам замер не бесплатен: на 4K long-GOP один шаг и есть та
/// самая секунда, поэтому результат запоминается по ключу файла, а для all-intra
/// кодеков не делается вовсе.
/// </summary>
public static class StepSpeedProbe
{
    /// <summary>Кодеки, у которых каждый кадр и так ключевой — мерить нечего.</summary>
    private static readonly string[] AllIntraCodecs =
        ["mjpeg", "prores", "dnxhd", "rawvideo", "huffyuv", "ffv1", "jpeg2000",
         "cfhd", "v210", "dvvideo", "qtrle", "png", "tiff", "bmp", "utvideo"];

    private const int Steps = 3;

    public static bool IsAllIntra(MediaInfo media) =>
        AllIntraCodecs.Any(c => media.Codec.Contains(c, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Замерить шаг назад в середине ролика и вернуть худшее из нескольких измерений.
    /// Позиция восстанавливается, воспроизведение при этом встаёт (шаг всегда ставит паузу).
    /// </summary>
    public static double Measure(IPlaybackBackend backend, MediaInfo media)
    {
        if (media.FrameCount < 4) return 0;

        var restore = backend.FrameIndex;

        // Середина ролика: у начала кадр обычно рядом с ключевым, и шаг назад
        // там неправдоподобно быстрый.
        var target = Math.Clamp(media.FrameCount / 2, Steps, media.FrameCount - 1);
        backend.SeekToFrame(target);

        var worst = 0.0;
        for (var i = 0; i < Steps; i++)
        {
            var sw = Stopwatch.StartNew();
            backend.StepBack();
            sw.Stop();
            worst = Math.Max(worst, sw.Elapsed.TotalMilliseconds);
        }

        backend.SeekToFrame(restore);
        return worst;
    }
}

/// <summary>
/// Запомненные замеры: файл открывают многократно, а мерить его стоит один раз.
/// Хранится в профиле пользователя рядом с настройками.
/// </summary>
public static class ProbeCache
{
    private const int MaxRecords = 200;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private static Dictionary<string, Record>? _records;

    public sealed class Record
    {
        public double StepBackMs { get; set; }
        public DateTime MeasuredUtc { get; set; }
    }

    public static double? Get(string key) =>
        Load().TryGetValue(key, out var record) ? record.StepBackMs : null;

    public static void Set(string key, double stepBackMs)
    {
        var records = Load();
        records[key] = new Record { StepBackMs = stepBackMs, MeasuredUtc = DateTime.UtcNow };

        if (records.Count > MaxRecords)
        {
            foreach (var stale in records.OrderBy(r => r.Value.MeasuredUtc).Take(records.Count - MaxRecords).ToList())
                records.Remove(stale.Key);
        }

        try
        {
            Directory.CreateDirectory(AppEnv.DataDir);
            File.WriteAllText(AppEnv.ProbeFile, JsonSerializer.Serialize(records, Options));
        }
        catch (Exception)
        {
            // Не записался — в следующий раз просто померим заново.
        }
    }

    private static Dictionary<string, Record> Load()
    {
        if (_records is not null) return _records;

        try
        {
            if (File.Exists(AppEnv.ProbeFile))
                _records = JsonSerializer.Deserialize<Dictionary<string, Record>>(File.ReadAllText(AppEnv.ProbeFile));
        }
        catch (Exception)
        {
            // Битый файл замеров — начинаем с пустого.
        }

        return _records ??= [];
    }
}
