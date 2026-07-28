using System.IO;
using System.Text.Json.Serialization;

namespace ComparisonPlayer.Cache;

/// <summary>
/// Запись кэша одного ролика: либо собранный all-intra прокси, либо полоска превью
/// (<see cref="ThumbnailsOnly"/>) — превью снимаются и без прокси, поэтому живут
/// отдельной записью со своим ключом. Сериализуется в entry.json внутри своей папки,
/// поэтому свойства изменяемые. Сведения об исходнике продублированы здесь намеренно:
/// по записи кэша плеер должен показать файл, даже не открывая оригинал демуксером.
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

    /// <summary>Имя файла прокси внутри папки записи; пусто у записи с одними превью.</summary>
    public string ProxyFile { get; set; } = "proxy.mp4";

    /// <summary>
    /// Запись — это только полоска превью, прокси в ней нет. Такие записи снимаются
    /// в любом режиме, в том числе когда кэш кадров выключен: клипу на таймлайне
    /// больше нечего показать, а весят они мегабайты против гигабайтов прокси.
    /// </summary>
    public bool ThumbnailsOnly { get; set; }

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
