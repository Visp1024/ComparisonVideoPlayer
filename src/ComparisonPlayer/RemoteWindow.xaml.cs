using System.Windows;
using System.Windows.Media;
using ComparisonPlayer.Chrome;
using ComparisonPlayer.Remote;

namespace ComparisonPlayer;

/// <summary>
/// Настройки внешнего управления и журнал последних событий (задача #9). Как и окно
/// настроек, правит копию: отмена должна отменять. Журнал живой — по нему видно,
/// доходят ли триггеры, не переключаясь в Unity.
/// </summary>
public partial class RemoteWindow : AppWindow
{
    private readonly RemoteLog _log;
    private readonly Func<int?> _clients;

    /// <param name="clients">
    /// Сколько клиентов подключено; null — сервер не поднят. Функцией, а не числом:
    /// окно живёт, пока состояние меняется.
    /// </param>
    public RemoteWindow(bool enabled, string pipeName, RemoteLog log, Func<int?> clients)
    {
        InitializeComponent();

        _log = log;
        _clients = clients;

        ChkEnabled.IsChecked = enabled;
        TxtPipe.Text = pipeName;

        _log.Changed += OnLogChanged;
        Closed += (_, _) => _log.Changed -= OnLogChanged;

        RefreshLog();
        RefreshState();
    }

    public bool ResultEnabled { get; private set; }

    public string ResultPipeName { get; private set; } = "cvp";

    private void OnLogChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(() => { RefreshLog(); RefreshState(); });

    private void RefreshLog() =>
        LogList.ItemsSource = _log.Entries.Select(e => $"{e.At:HH:mm:ss}  {e.Message}").ToArray();

    private void RefreshState()
    {
        var clients = _clients();

        (TxtState.Text, var brush) = clients switch
        {
            null => ("выключено", "DimBrush"),
            0 => ("ждёт подключения", "WarnBrush"),
            1 => ("подключён клиент", "OkBrush"),
            var n => ($"подключено клиентов: {n}", "OkBrush")
        };

        TxtState.Foreground = (Brush)FindResource(brush);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = TxtPipe.Text.Trim();

        // Пустое имя дало бы \\.\pipe\ — канал, который не откроется. Проще вернуть
        // умолчание, чем показывать ошибку из-за очевидной опечатки.
        ResultPipeName = string.IsNullOrEmpty(name) ? "cvp" : name;
        ResultEnabled = ChkEnabled.IsChecked == true;

        DialogResult = true;
    }
}
