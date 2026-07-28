using System.Windows;

namespace ComparisonPlayer.Chrome;

/// <summary>
/// Своё окно сообщения вместо <c>MessageBox</c> (задача #21): системное окно белое,
/// с чужой иконкой и чужим шрифтом — на фоне тёмного приложения оно выглядит окном
/// другой программы. Показывает одну мысль и, отдельной строкой, подробность вроде пути.
/// </summary>
public partial class MessageDialog : AppWindow
{
    private MessageDialog(string caption, string message, string? detail)
    {
        InitializeComponent();

        Title = caption;
        Bar.Caption = caption;
        TxtMessage.Text = message;

        if (!string.IsNullOrWhiteSpace(detail))
        {
            TxtDetail.Text = detail;
            TxtDetail.Visibility = Visibility.Visible;
        }
    }

    /// <summary>
    /// Показать сообщение. Без владельца (ошибка на запуске, окна ещё нет) встаёт по центру экрана.
    /// </summary>
    public static void Show(Window? owner, string caption, string message, string? detail = null)
    {
        var dialog = new MessageDialog(caption, message, detail);

        if (owner is not null && owner.IsLoaded)
            dialog.Owner = owner;
        else
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        dialog.ShowDialog();
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => Close();
}
