using System.IO;
using System.Text.Json;

namespace ComparisonPlayer.Cache;

/// <summary>
/// Хранилище кэша: папка на запись, entry.json внутри, лимит объёма с вытеснением LRU.
/// Отдельного файла-индекса нет намеренно — он рассинхронизируется при аварийном
/// завершении, а обход десятка папок стоит миллисекунды.
/// </summary>
public sealed class FrameCacheStore
{
    private const string EntryFile = "entry.json";
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public FrameCacheStore(string? root = null) => Root = root ?? AppEnv.CacheDir;

    public string Root { get; }

    public string DirectoryFor(string key) => Path.Combine(Root, key);

    /// <summary>Готовая запись с прокси либо null: нет папки, нет прокси или битый entry.json.</summary>
    public CacheEntry? Find(string key)
    {
        var entry = Read(DirectoryFor(key));
        return entry is { ThumbnailsOnly: false } && File.Exists(entry.ProxyPath) ? entry : null;
    }

    /// <summary>
    /// Готовая полоска превью по её ключу либо null. Ключ у превью свой, от параметров
    /// прокси не зависящий, поэтому запись переживает смену частоты кэша.
    /// </summary>
    public CacheEntry? FindThumbnails(string key)
    {
        var entry = Read(DirectoryFor(key));
        return entry is { ThumbnailsOnly: true } && entry.ThumbnailFiles().Count > 0 ? entry : null;
    }

    public void Save(CacheEntry entry)
    {
        Directory.CreateDirectory(entry.Directory);
        entry.Bytes = DirectorySize(entry.Directory);
        File.WriteAllText(Path.Combine(entry.Directory, EntryFile), JsonSerializer.Serialize(entry, Options));
    }

    /// <summary>Отметить запись использованной — это её защита от вытеснения.</summary>
    public void Touch(CacheEntry entry)
    {
        entry.LastUsedUtc = DateTime.UtcNow;
        try { Save(entry); }
        catch (Exception) { /* не смогли отметить — это не повод не играть из кэша */ }
    }

    public IReadOnlyList<CacheEntry> All()
    {
        if (!Directory.Exists(Root)) return [];

        return Directory.GetDirectories(Root)
            .Select(Read)
            .OfType<CacheEntry>()
            .OrderByDescending(e => e.LastUsedUtc)
            .ToList();
    }

    public long TotalBytes() => All().Sum(e => e.Bytes);

    /// <summary>
    /// Убрать самые давно не открывавшиеся записи, пока объём не уложится в лимит.
    /// Записи <paramref name="keepKeys"/> не трогаем никогда: это открытые сейчас файлы,
    /// у каждого из которых своих записей две — прокси и полоска превью.
    /// </summary>
    /// <returns>Сколько записей удалено.</returns>
    public int Trim(long limitBytes, params string?[] keepKeys)
    {
        if (limitBytes <= 0) return 0;   // лимит выключен

        var keep = keepKeys.Where(k => !string.IsNullOrEmpty(k)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var entries = All().OrderByDescending(e => e.LastUsedUtc).ToList();
        var total = entries.Sum(e => e.Bytes);
        var removed = 0;

        // Идём с конца — от самой давней записи.
        for (var i = entries.Count - 1; i >= 0 && total > limitBytes; i--)
        {
            var entry = entries[i];
            if (keep.Contains(entry.Key)) continue;

            if (!Remove(entry.Key)) continue;
            total -= entry.Bytes;
            removed++;
        }

        return removed;
    }

    public bool Remove(string key)
    {
        try
        {
            var dir = DirectoryFor(key);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            return true;
        }
        catch (Exception)
        {
            // Папка занята играющим прокси — удалим при следующей уборке.
            return false;
        }
    }

    /// <summary>
    /// Очистить всё, кроме перечисленных записей. Беречь приходится несколько:
    /// с фазы 3 открытых файлов два, и прокси каждого нельзя удалять из-под плеера.
    /// </summary>
    public int Clear(params string?[] keepKeys)
    {
        var keep = keepKeys.Where(k => !string.IsNullOrEmpty(k)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return All().Count(e => !keep.Contains(e.Key) && Remove(e.Key));
    }

    private static CacheEntry? Read(string dir)
    {
        try
        {
            var file = Path.Combine(dir, EntryFile);
            if (!File.Exists(file)) return null;

            var entry = JsonSerializer.Deserialize<CacheEntry>(File.ReadAllText(file));
            if (entry is null) return null;

            entry.Directory = dir;
            if (string.IsNullOrEmpty(entry.Key)) entry.Key = Path.GetFileName(dir);
            return entry;
        }
        catch (Exception)
        {
            // Битую запись просто не видим: пересоберётся.
            return null;
        }
    }

    private static long DirectorySize(string dir)
    {
        try
        {
            return new DirectoryInfo(dir)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(f => f.Length);
        }
        catch (Exception)
        {
            return 0;
        }
    }
}
