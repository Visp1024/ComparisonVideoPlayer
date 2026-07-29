using System.Windows;
using System.Windows.Controls;

namespace ComparisonPlayer.Chrome;

/// <summary>
/// Титульная полоса окна (задача #21): иконка и название слева, что открыто — по центру,
/// кнопки окна справа. Работает в паре с <see cref="AppWindow"/>: перетаскивание и двойной
/// клик остаются за Windows, полоса отвечает только за вид и за три кнопки.
/// </summary>
public partial class TitleBar : UserControl
{
    public static readonly DependencyProperty CaptionProperty = DependencyProperty.Register(
        nameof(Caption), typeof(string), typeof(TitleBar),
        new PropertyMetadata("", (d, e) => ((TitleBar)d).CaptionText.Text = (string)e.NewValue));

    /// <summary>Диалогам «свернуть» и «развернуть» не нужны — у них только закрытие.</summary>
    public static readonly DependencyProperty ShowResizeButtonsProperty = DependencyProperty.Register(
        nameof(ShowResizeButtons), typeof(bool), typeof(TitleBar),
        new PropertyMetadata(true, (d, e) => ((TitleBar)d).UpdateButtons()));

    public string Caption
    {
        get => (string)GetValue(CaptionProperty);
        set => SetValue(CaptionProperty, value);
    }

    public bool ShowResizeButtons
    {
        get => (bool)GetValue(ShowResizeButtonsProperty);
        set => SetValue(ShowResizeButtonsProperty, value);
    }

    private Window? _window;

    public TitleBar()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_window is not null) return;

        _window = Window.GetWindow(this);
        if (_window is null) return;

        _window.StateChanged += (_, _) => UpdateButtons();
        UpdateButtons();
    }

    /// <summary>
    /// Показать в полосе, что открыто. Пустое имя убирает и букву трека, и разделитель:
    /// имена файлов задаются вместе, потому что разделитель нужен только между двумя.
    /// Буквы появляются лишь когда открыты оба ролика — одному имени файла помечать
    /// нечего (задача #32).
    /// </summary>
    public void ShowFiles(string? fileA, string? pathA, string? fileB, string? pathB)
    {
        var hasA = !string.IsNullOrEmpty(fileA);
        var hasB = !string.IsNullOrEmpty(fileB);
        var letters = hasA && hasB;

        LetterA.Visibility = letters ? Visibility.Visible : Visibility.Collapsed;
        NameA.Text = fileA ?? "";
        NameA.ToolTip = pathA;

        LetterB.Visibility = letters ? Visibility.Visible : Visibility.Collapsed;
        NameB.Text = fileB ?? "";
        NameB.ToolTip = pathB;

        Divider.Visibility = hasA && hasB ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Развернуть и восстановить — одна кнопка на месте: видна та, что уместна сейчас.</summary>
    private void UpdateButtons()
    {
        var maximized = _window?.WindowState == WindowState.Maximized;
        var resizable = ShowResizeButtons && _window?.ResizeMode is not (ResizeMode.NoResize or ResizeMode.CanMinimize);

        BtnMinimize.Visibility = ShowResizeButtons ? Visibility.Visible : Visibility.Collapsed;
        BtnMaximize.Visibility = resizable && !maximized ? Visibility.Visible : Visibility.Collapsed;
        BtnRestore.Visibility = resizable && maximized ? Visibility.Visible : Visibility.Collapsed;
    }
}
