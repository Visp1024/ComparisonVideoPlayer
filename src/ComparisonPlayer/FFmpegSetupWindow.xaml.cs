using System.ComponentModel;
using System.Net.Http;
using System.Windows;
using System.Windows.Input;
using ComparisonPlayer.Chrome;
using ComparisonPlayer.Localization;

namespace ComparisonPlayer;

/// <summary>
/// Окно первого запуска на машине без FFmpeg: предлагает скачать комплект библиотек и
/// положить его рядом с программой. Показывается до старта движка — если пользователь
/// откажется, запуск пойдёт прежним путём и упрётся в ошибку Engine.Start с подсказкой.
/// </summary>
public partial class FFmpegSetupWindow : AppWindow
{
    private CancellationTokenSource? _cancel;

    /// <summary>Закрыть окно, как только прерванная загрузка свернётся (пользователь нажал крестик).</summary>
    private bool _closeWhenIdle;

    /// <summary>Каталог, куда легли библиотеки. Заполняется только при успехе.</summary>
    public string? InstalledDir { get; private set; }

    public FFmpegSetupWindow()
    {
        InitializeComponent();
        TxtTarget.Text = FFmpegInstaller.TargetDir;
        BtnInstall.Content = Loc.Str("Setup.Download", FFmpegInstaller.ApproxDownloadMb);
        TxtStatus.Text = Loc.Str("Setup.Once");
    }

    private bool Busy => _cancel is not null;

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (Busy) return;

        _cancel = new CancellationTokenSource();
        BtnInstall.IsEnabled = false;
        BtnCancel.Content = Loc.Str("Setup.Cancel");
        Bar.Visibility = Visibility.Visible;
        Bar.Value = 0;
        TxtStatus.Text = Loc.Str("Setup.Connecting");

        // Progress<T> отдаёт отчёты в поток, где создан, — здесь это поток окна,
        // поэтому обращаться к элементам напрямую можно без Dispatcher.
        var progress = new Progress<FFmpegInstallProgress>(Report);

        try
        {
            InstalledDir = await FFmpegInstaller.InstallAsync(progress, _cancel.Token);
            _cancel = null;
            DialogResult = true;               // закрывает диалог само
            return;
        }
        catch (OperationCanceledException)
        {
            TxtStatus.Text = Loc.Str("Setup.Cancelled");
        }
        catch (HttpRequestException ex)
        {
            // Сеть — самая частая причина отказа, и звучать она должна отдельно от
            // «архив битый»: пользователю нужно понять, чинить ли ему интернет.
            Fail(Loc.Str("Setup.Failed", ex.Message));
        }
        catch (Exception ex)
        {
            Fail(ex.Message);
        }

        _cancel = null;
        if (_closeWhenIdle) { Close(); return; }

        Bar.Visibility = Visibility.Collapsed;
        BtnInstall.IsEnabled = true;
        BtnInstall.Content = Loc.Str("Setup.Retry");
        BtnCancel.Content = Loc.Str("Setup.NotNow");
    }

    private void Report(FFmpegInstallProgress p)
    {
        if (p.Stage == FFmpegInstallStage.Extract)
        {
            Bar.Value = p.Total > 0 ? 100.0 * p.Done / p.Total : 0;
            TxtStatus.Text = Loc.Str("Setup.Extracting", p.Done, p.Total);
            return;
        }

        const double mb = 1024 * 1024;
        if (p.Total > 0)
        {
            Bar.Value = 100.0 * p.Done / p.Total;
            TxtStatus.Text = Loc.Str("Setup.Downloading", $"{p.Done / mb:0.0}", $"{p.Total / mb:0.0}");
        }
        else
        {
            // Сервер не назвал размер — доли нет, показываем хотя бы счётчик мегабайт.
            Bar.Value = 0;
            TxtStatus.Text = Loc.Str("Setup.DownloadingUnknown", $"{p.Done / mb:0.0}");
        }
    }

    private void Fail(string message)
    {
        TxtStatus.Text = Loc.Str("Setup.FailTail", message);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (Busy) _cancel!.Cancel();
        else Close();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        // Esc во время загрузки прерывает её, а не закрывает окно посреди записи файла.
        if (e.Key == Key.Escape)
        {
            Cancel_Click(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }
        base.OnPreviewKeyDown(e);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Крестик на идущей загрузке: отменяем её и остаёмся на месте — закроется само,
        // когда установка свернётся, иначе временный файл останется недописанным.
        if (Busy)
        {
            _closeWhenIdle = true;
            _cancel!.Cancel();
            e.Cancel = true;
            return;
        }
        base.OnClosing(e);
    }
}
