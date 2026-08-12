using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ComparisonPlayer;

/// <summary>Состояние одного трека в сохранённой сессии.</summary>
public sealed class SessionTrack
{
    /// <summary>Путь к ролику; null — трек был пуст.</summary>
    public string? File { get; set; }

    /// <summary>Сдвиг трека относительно мастер-клока, секунд (PLAN.md §4.2).</summary>
    public double OffsetSeconds { get; set; }

    /// <summary>Начало отрезка воспроизведения, в кадрах самого трека.</summary>
    public long InFrame { get; set; }

    /// <summary>
    /// Конец отрезка, в кадрах трека; −1 — «до конца ролика». Число кадров станет
    /// известно только после открытия файла, а оно может и не совпасть с прошлым
    /// запуском (смена частоты прокси), поэтому полный отрезок пишется признаком.
    /// </summary>
    public long OutFrame { get; set; } = -1;
}

/// <summary>
/// Сессия сравнения: какие ролики открыты, как они выровнены, какие у них отрезки
/// и где стоял playhead. Восстанавливается при запуске и переносится между машинами
/// файлом — путь к роликам в ней абсолютный.
/// </summary>
public sealed class Session
{
    /// <summary>Расширение и фильтр диалога для сессии, сохранённой отдельным файлом.</summary>
    public const string FileExtension = ".cvp.json";

    /// <summary>Свойством, а не константой: подписи фильтра приходят из словаря языка.</summary>
    public static string FileFilter => Localization.Loc.Str("Session.Filter");

    public SessionTrack A { get; set; } = new();
    public SessionTrack B { get; set; } = new();

    /// <summary>Трек-мастер: его кадрами меряется шаг.</summary>
    public string Master { get; set; } = "A";

    /// <summary>Активный трек: ему адресованы открытие файла и правка отрезка.</summary>
    public string Active { get; set; } = "A";

    /// <summary>Позиция playhead в кадрах общей шкалы.</summary>
    public long PositionFrame { get; set; }

    public double Speed { get; set; } = 1;

    public bool Loop { get; set; }

    /// <summary>Что показано в области кадра: <see cref="LayoutMode"/> словом.</summary>
    public string Layout { get; set; } = nameof(LayoutMode.Side);

    public DateTime SavedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Есть ли что восстанавливать: сессия без единого файла бесполезна.</summary>
    [JsonIgnore]
    public bool HasFiles => !string.IsNullOrEmpty(A.File) || !string.IsNullOrEmpty(B.File);

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Прочитать сессию из файла; null — файла нет или он не читается.</summary>
    public static Session? Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<Session>(File.ReadAllText(path), Options);
        }
        catch (Exception)
        {
            // Битая сессия не должна мешать запуску: работаем с пустыми треками.
            return null;
        }
    }

    /// <summary>Записать сессию; возвращает причину отказа или пусто при успехе.</summary>
    public string Save(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            SavedUtc = DateTime.UtcNow;
            File.WriteAllText(path, JsonSerializer.Serialize(this, Options));
            return "";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>Удалить сохранённую сессию (её нечего восстанавливать).</summary>
    public static void Delete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception)
        {
            // Не смогли — значит, при следующем запуске восстановится прошлое состояние.
        }
    }
}
