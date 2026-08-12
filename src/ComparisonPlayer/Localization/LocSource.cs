using System.ComponentModel;

namespace ComparisonPlayer.Localization;

/// <summary>
/// Источник строк для привязок разметки: <c>{loc:Str Ключ}</c> превращается в привязку
/// к его индексатору. Смена языка сообщается как изменение индексатора целиком —
/// WPF перечитывает все такие привязки, и подписи меняются без перезапуска окон.
/// </summary>
public sealed class LocSource : INotifyPropertyChanged
{
    /// <summary>Единственный экземпляр: привязок к нему тысячи, а состояние у него общее.</summary>
    public static LocSource Instance { get; } = new();

    private LocSource() { }

    public string this[string key] => Loc.Str(key);

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>«Item[]» — принятое в WPF имя для «изменился весь индексатор».</summary>
    internal void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
}
