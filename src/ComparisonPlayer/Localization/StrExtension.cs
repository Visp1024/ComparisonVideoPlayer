using System.Windows.Data;
using System.Windows.Markup;

namespace ComparisonPlayer.Localization;

/// <summary>
/// Строка интерфейса в разметке: <c>Content="{loc:Str Toolbar.File}"</c>.
/// </summary>
/// <remarks>
/// Отдаёт не саму строку, а привязку к <see cref="LocSource"/>: подставленная один раз
/// строка так и осталась бы на прежнем языке, а привязка переживает переключение языка
/// на ходу. Поэтому же расширение годится везде, где WPF принимает привязку, — свойство
/// элемента, шаблон, значение сеттера.
/// </remarks>
[MarkupExtensionReturnType(typeof(string))]
public sealed class StrExtension : MarkupExtension
{
    public StrExtension() { }

    public StrExtension(string key) => Key = key;

    /// <summary>Ключ словаря; пустой ключ вернёт пустую строку, а не сорвёт разбор разметки.</summary>
    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding($"[{Key}]")
        {
            Source = LocSource.Instance,
            Mode = BindingMode.OneWay
        };

        return binding.ProvideValue(serviceProvider);
    }
}
