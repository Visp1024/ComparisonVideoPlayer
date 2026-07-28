using System.IO;
using System.Text.Json.Serialization;

namespace ComparisonPlayer.Cache;

/// <summary>
/// Запись кэша: собранный all-intra прокси одного ролика и его миниатюры.
/// Сериализуется в entry.json рядом с прокси, поэтому свойства изменяемые.
/// Сведения об исходнике продублированы здесь намеренно: по записи кэша
/// плеер должен показать файл, даже не открывая оригинал демуксером.
/// </summary>
public sealed class CacheEntry
{
    public string Key { get; set; } = "";

    public string SourcePath { get; set; } = "";
    public long SourceLength { get; set; }

    /// <summary>Кодек исходника (не прокси) — его показывает панель сведений.</summary>
    public string Codec { get; set; } = "";

    public int Width { get; set; }
    public int Height { get; set; }
    /// <summary>Частота исходника — она может отличаться от частоты прокси.</summary>
    public double SourceFps { get; set; }

    /// <summary>Частота прокси и число его кадров: именно в них работает транспорт.</summary>
    public double Fps { get; set; }
    public long FrameCount { get; set; }
    public long DurationTicks { get; set; }

    /// <summary>Имя файла прокси внутри папки записи.</summary>
    public string ProxyFile { get; set; } = "proxy.mp4";

    public int ThumbnailCount { get; set; }
    public double ThumbnailIntervalSeconds { get; set; }

    /// <summary>Подпись параметров сборки — та же, что участвует в ключе.</summary>
    public string Parameters { get; set; } = "";

    public DateTime CreatedUtc { get; set; }

    /// <summary>Последнее открытие: по нему идёт вытеснение LRU.</summary>
    public DateTime LastUsedUtc { get; set; }

    /// <summary>Объём папки записи; считается при сохранении.</summary>
    public long Bytes { get; set; }

    /// <summary>Папка записи. В entry.json не пишется — она и есть эта папка.</summary>
    [JsonIgnore] public string Directory { get; set; } = "";

    /// <summary>
    /// Запись описывает ещё собираемый прокси: файл растёт, кадров в нём меньше,
    /// чем <see cref="FrameCount"/>. На диск такая запись не сохраняется — её
    /// собирает окно, чтобы играть кэш, не дожидаясь конца сборки.
    /// </summary>
    [JsonIgnore] public bool Partial { get; set; }

    [JsonIgnore] public string ProxyPath => Path.Combine(Directory, ProxyFile);
    [JsonIgnore] public string ThumbnailDirectory => Path.Combine(Directory, "thumbs");
    [JsonIgnore] public TimeSpan Duration => TimeSpan.FromTicks(DurationTicks);
    [JsonIgnore] public string SourceName => Path.GetFileName(SourcePath);

    /// <summary>Файлы миниатюр по порядку; пусто, если их не собирали.</summary>
    public IReadOnlyList<string> ThumbnailFiles()
    {
        if (!System.IO.Directory.Exists(ThumbnailDirectory)) return [];

        var files = System.IO.Directory.GetFiles(ThumbnailDirectory, "*.jpg");
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);   // имена нумерованные, порядок = порядок кадров
        return files;
    }
}
