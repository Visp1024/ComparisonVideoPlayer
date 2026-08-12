using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace ComparisonPlayer.Localization;

/// <summary>Язык интерфейса: код (<c>en</c>, <c>ru</c>) и его собственное название для списка выбора.</summary>
/// <param name="Code">Код языка — он же суффикс файла словаря и значение в настройках.</param>
/// <param name="Name">Название на самом языке: «Русский», а не «Russian» — так его узнают те, кому он нужен.</param>
public sealed record LanguageInfo(string Code, string Name);

/// <summary>
/// Строки интерфейса. Словарь на язык — плоский JSON-файл <c>Strings.&lt;код&gt;.json</c>:
/// встроенные языки лежат ресурсами в сборке, а рядом с программой их можно дополнить
/// или заменить своими файлами — новый язык не требует пересборки.
/// </summary>
/// <remarks>
/// Ключи дотированные («Toolbar.File»), значения — обычные строки .NET-формата: аргументы
/// подставляются через <see cref="Str(string, object?[])"/>. Пропущенный ключ не ломает
/// окно: берём английский вариант, а нет и его — сам ключ, чтобы недостача была видна.
/// </remarks>
public static class Loc
{
    /// <summary>Значение настройки «как в системе»: язык берётся из Windows при каждом запуске.</summary>
    public const string SystemLanguage = "";

    /// <summary>Язык, на котором словарь есть всегда: он же запасной для пропущенных ключей.</summary>
    public const string FallbackLanguage = "en";

    /// <summary>Встроенные языки. Их файлы копируются и рядом с программой — как образец для своих.</summary>
    private static readonly string[] BuiltIn = ["en", "ru"];

    private static readonly ConcurrentDictionary<string, Dictionary<string, string>> Loaded = new();

    private static Dictionary<string, string> _current = [];
    private static Dictionary<string, string> _fallback = [];

    /// <summary>Действующий код языка — уже разрешённый, «как в системе» сюда не попадает.</summary>
    public static string CurrentCode { get; private set; } = FallbackLanguage;

    /// <summary>Язык сменился: код, расставивший подписи сам, пересобирает их заново.</summary>
    public static event EventHandler? Changed;

    /// <summary>
    /// Языки, на которые можно переключиться: встроенные плюс найденные рядом с программой.
    /// Читается с диска один раз — список языков за время работы не меняется.
    /// </summary>
    public static IReadOnlyList<LanguageInfo> Available => _available ??= FindLanguages();

    private static IReadOnlyList<LanguageInfo>? _available;

    /// <summary>
    /// Выбрать язык. <c>null</c> или <see cref="SystemLanguage"/> — язык Windows; язык без
    /// словаря откатывается к английскому, иначе опечатка в настройках оставила бы окно
    /// без подписей вовсе.
    /// </summary>
    public static void Use(string? code)
    {
        var wanted = string.IsNullOrWhiteSpace(code) ? SystemCode() : code.Trim();

        _fallback = Dictionary(FallbackLanguage);
        _current = Dictionary(wanted);

        // Словаря нет — язык не выбран, а притворяться выбранным ему нельзя: окно
        // настроек показало бы галочку у языка, которого пользователь не увидит.
        CurrentCode = _current.Count > 0 ? wanted : FallbackLanguage;
        if (_current.Count == 0) _current = _fallback;

        LocSource.Instance.Refresh();
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Строка по ключу; пропущенный ключ виден в интерфейсе как сам ключ.</summary>
    public static string Str(string key) =>
        _current.TryGetValue(key, out var value) ? value
        : _fallback.TryGetValue(key, out var spare) ? spare
        : key;

    /// <summary>
    /// Строка с подстановкой: значение — обычный формат .NET (<c>{0}</c>). Формат берём
    /// инвариантный — числа в подписях уже приведены к виду вызывающим кодом.
    /// </summary>
    public static string Str(string key, params object?[] args)
    {
        var format = Str(key);

        try
        {
            return string.Format(CultureInfo.InvariantCulture, format, args);
        }
        catch (FormatException)
        {
            // Испорченный чужой перевод («{0» вместо «{0}») не должен ронять окно:
            // показываем формат как есть — по нему видно, какую строку чинить.
            return format;
        }
    }

    // ---------- словари ----------

    private static Dictionary<string, string> Dictionary(string code) =>
        Loaded.GetOrAdd(code, LoadDictionary);

    /// <summary>
    /// Словарь языка: сначала файл рядом с программой (его можно править и добавлять),
    /// затем встроенный ресурс. Битый файл считаем отсутствующим — запуск важнее перевода.
    /// </summary>
    private static Dictionary<string, string> LoadDictionary(string code)
    {
        var path = FilePath(code);

        if (File.Exists(path))
        {
            var custom = Parse(() => File.OpenRead(path));
            if (custom is not null) return custom;
        }

        return Parse(() => Embedded(code)) ?? [];
    }

    private static Dictionary<string, string>? Parse(Func<Stream?> open)
    {
        try
        {
            using var stream = open();
            if (stream is null) return null;

            var raw = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(stream);
            if (raw is null) return null;

            // Значения не-строки (случайный номер, вложенный объект) пропускаем: на такой
            // ключ придёт запасной перевод, а не исключение посреди разбора XAML.
            return raw
                .Where(pair => pair.Value.ValueKind == JsonValueKind.String)
                .ToDictionary(pair => pair.Key, pair => pair.Value.GetString()!);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static Stream? Embedded(string code) =>
        typeof(Loc).Assembly.GetManifestResourceStream($"ComparisonPlayer.Strings.Strings.{code}.json");

    private static string FilePath(string code) =>
        Path.Combine(AppContext.BaseDirectory, "Strings", $"Strings.{code}.json");

    // ---------- список языков ----------

    /// <summary>
    /// Встроенные языки плюс всё, что нашлось в каталоге <c>Strings</c> рядом с программой:
    /// положить туда <c>Strings.de.json</c> достаточно, чтобы язык появился в настройках.
    /// </summary>
    private static IReadOnlyList<LanguageInfo> FindLanguages()
    {
        var codes = new List<string>(BuiltIn);

        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "Strings");
            if (Directory.Exists(dir))
                codes.AddRange(Directory
                    .EnumerateFiles(dir, "Strings.*.json")
                    .Select(f => Path.GetFileNameWithoutExtension(f)["Strings.".Length..])
                    .Where(c => c.Length > 0 && !codes.Contains(c, StringComparer.OrdinalIgnoreCase)));
        }
        catch (Exception)
        {
            // Каталог недоступен — обходимся встроенными языками.
        }

        return codes
            .Select(c => new LanguageInfo(c, LanguageName(c)))
            .ToArray();
    }

    /// <summary>
    /// Название языка для списка: из самого словаря (ключ <c>Language.Name</c>), а нет его —
    /// у Windows. Так чужой файл сам решает, как ему называться.
    /// </summary>
    private static string LanguageName(string code)
    {
        if (Dictionary(code).TryGetValue("Language.Name", out var name) && name.Length > 0)
            return name;

        try
        {
            var native = CultureInfo.GetCultureInfo(code).NativeName;
            return native.Length > 0 ? char.ToUpper(native[0], CultureInfo.InvariantCulture) + native[1..] : code;
        }
        catch (CultureNotFoundException)
        {
            return code;
        }
    }

    private static string SystemCode() => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
}
