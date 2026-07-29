namespace ComparisonPlayer.Remote;

/// <summary>Одна строка журнала внешнего управления.</summary>
public readonly record struct RemoteLogEntry(DateTime At, string Message);

/// <summary>
/// Последние события внешнего управления для модалки. Живёт в памяти и обрезается:
/// журнал нужен, чтобы ответить «дошёл ли триггер прямо сейчас», а не для истории.
/// </summary>
public sealed class RemoteLog
{
    public const int Capacity = 20;

    private readonly List<RemoteLogEntry> _entries = [];
    private readonly Lock _gate = new();

    /// <summary>Новее — первым: модалка показывает список сверху вниз.</summary>
    public IReadOnlyList<RemoteLogEntry> Entries
    {
        get { lock (_gate) return _entries.ToArray(); }
    }

    /// <summary>Поднимается из потока сервера — подписчик обязан уйти в Dispatcher сам.</summary>
    public event EventHandler? Changed;

    public void Add(string message)
    {
        lock (_gate)
        {
            _entries.Insert(0, new RemoteLogEntry(DateTime.Now, message));
            if (_entries.Count > Capacity) _entries.RemoveRange(Capacity, _entries.Count - Capacity);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
