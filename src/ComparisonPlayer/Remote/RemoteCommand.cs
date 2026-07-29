using System.Text.Json;

namespace ComparisonPlayer.Remote;

/// <summary>Что просит сделать внешний клиент (Unity). Протокол — спека задачи #9.</summary>
public enum RemoteCommandKind
{
    /// <summary>Воспроизведение; <see cref="RemoteCommand.Flag"/> — начинать ли с in-точки.</summary>
    Play,

    /// <summary>Пауза на месте.</summary>
    Pause,

    /// <summary>Пауза и возврат на начало отрезка.</summary>
    Stop,

    /// <summary>Встать на начало отрезка, не запуская.</summary>
    Rewind,

    /// <summary>Петля по отрезку; <see cref="RemoteCommand.Flag"/> — включить или выключить.</summary>
    Loop
}

/// <summary>
/// Команда внешнего управления. Протокол намеренно плоский: одна строка JSON = одна
/// команда, никаких вложенных структур — его пишут руками при отладке.
/// </summary>
/// <param name="Flag">
/// Единственный параметр протокола: для <see cref="RemoteCommandKind.Play"/> это
/// «с начала отрезка», для <see cref="RemoteCommandKind.Loop"/> — «включить».
/// Держать два одинаковых по типу поля с разными именами смысла нет.
/// </param>
public sealed record RemoteCommand(RemoteCommandKind Kind, bool Flag)
{
    /// <summary>
    /// Разобрать строку протокола. Никогда не бросает: битую строку присылает чужой
    /// процесс, и его ошибка не должна ронять плеер — она уходит в журнал текстом.
    /// </summary>
    public static bool TryParse(string line, out RemoteCommand? command, out string? error)
    {
        command = null;
        error = null;

        if (string.IsNullOrWhiteSpace(line)) { error = "пустая строка"; return false; }

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "ожидался объект JSON";
                return false;
            }

            if (!root.TryGetProperty("cmd", out var cmd) || cmd.ValueKind != JsonValueKind.String)
            {
                error = "нет поля cmd";
                return false;
            }

            var name = cmd.GetString()!;
            switch (name.ToLowerInvariant())
            {
                // fromStart по умолчанию true: триггер из игры — это «покажи момент
                // сначала», продолжение с текущей позиции нужно реже и просится явно.
                case "play": command = new RemoteCommand(RemoteCommandKind.Play, Bool(root, "fromStart", true)); return true;
                case "pause": command = new RemoteCommand(RemoteCommandKind.Pause, false); return true;
                case "stop": command = new RemoteCommand(RemoteCommandKind.Stop, false); return true;
                case "rewind": command = new RemoteCommand(RemoteCommandKind.Rewind, false); return true;
                case "loop": command = new RemoteCommand(RemoteCommandKind.Loop, Bool(root, "on", true)); return true;
                default: error = $"неизвестная команда «{name}»"; return false;
            }
        }
        catch (JsonException)
        {
            error = "строка не разбирается как JSON";
            return false;
        }
    }

    private static bool Bool(JsonElement root, string name, bool fallback) =>
        root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : fallback;

    /// <summary>Строка для журнала в модалке — её читает человек, а не программа.</summary>
    public string Describe() => Kind switch
    {
        RemoteCommandKind.Play => Flag ? "play с начала отрезка" : "play с текущей позиции",
        RemoteCommandKind.Pause => "pause",
        RemoteCommandKind.Stop => "stop",
        RemoteCommandKind.Rewind => "rewind",
        RemoteCommandKind.Loop => Flag ? "loop включена" : "loop выключена",
        _ => Kind.ToString()
    };
}
