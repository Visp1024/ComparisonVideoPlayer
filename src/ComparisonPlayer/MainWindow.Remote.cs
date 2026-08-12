using System.IO;
using System.Windows;
using System.Windows.Media;
using ComparisonPlayer.Localization;
using ComparisonPlayer.Remote;

namespace ComparisonPlayer;

/// <summary>
/// Внешнее управление (задача #9): команды из Unity применяются к тому же транспорту,
/// что клавиши и кнопки. Своей логики воспроизведения здесь нет и быть не должно —
/// иначе поведение по каналу начнёт расходиться с поведением плеера.
/// </summary>
public partial class MainWindow
{
    private readonly RemoteLog _remoteLog = new();
    private PipeServer? _remoteServer;

    private void InitRemote()
    {
        if (App.Settings.RemoteEnabled && !StartRemote(App.Settings.RemotePipeName))
        {
            // Канал занят ещё с прошлого запуска или другим плеером: настройка не
            // должна утверждать, что управление работает.
            App.Settings.RemoteEnabled = false;
            App.Settings.Save();
        }

        UpdateRemoteButton();
    }

    private void Unity_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new RemoteWindow(
            App.Settings.RemoteEnabled, App.Settings.RemotePipeName, _remoteLog,
            () => _remoteServer?.ClientCount) { Owner = this };

        if (dialog.ShowDialog() != true) return;

        App.Settings.RemotePipeName = dialog.ResultPipeName;

        if (dialog.ResultEnabled) App.Settings.RemoteEnabled = StartRemote(dialog.ResultPipeName);
        else { StopRemote(); App.Settings.RemoteEnabled = false; }

        App.Settings.Save();
        UpdateRemoteButton();

        if (dialog.ResultEnabled && !App.Settings.RemoteEnabled)
            Status(Loc.Str("Status.PipeBusy", dialog.ResultPipeName));
    }

    /// <summary>
    /// Подсветка кнопки — включено ли управление, цвет точки — есть ли клиент.
    /// Два разных вопроса, и пользователю нужны ответы на оба сразу.
    /// </summary>
    private void UpdateRemoteButton()
    {
        var running = _remoteServer is not null;
        var connected = _remoteServer is { ClientCount: > 0 };

        Highlight(BtnUnity, running);
        UnityDot.Fill = (Brush)FindResource(!running ? "DimBrush" : connected ? "OkBrush" : "WarnBrush");
    }

    /// <summary>Поднять сервер; false — имя канала занято другим процессом.</summary>
    private bool StartRemote(string pipeName)
    {
        StopRemote();

        var server = new PipeServer(pipeName, _remoteLog);
        server.CommandReceived += (_, command) => Dispatcher.BeginInvoke(() => ApplyRemote(command));
        server.ClientsChanged += (_, _) => Dispatcher.BeginInvoke(UpdateRemoteButton);

        try
        {
            server.Start();
            _remoteServer = server;
            return true;
        }
        catch (IOException)
        {
            // Имя занято другим плеером. Не падаем: причина уходит в журнал, который
            // пользователь видит в модалке, а тумблер откатывается — включённое
            // состояние без работающего канала врало бы.
            _remoteLog.Add(Loc.Str("Remote.LogPipeBusy", pipeName));
            server.Dispose();
            return false;
        }
    }

    private void StopRemote()
    {
        _remoteServer?.Dispose();
        _remoteServer = null;
    }

    private void ApplyRemote(RemoteCommand command)
    {
        if (!_sync.IsOpen)
        {
            _remoteLog.Add(Loc.Str("Remote.LogNoFile"));
            return;
        }

        switch (command.Kind)
        {
            case RemoteCommandKind.Play when command.Flag:
                SeekFrame(_sync.SegmentInFrame);
                PlayFromHere();
                break;

            case RemoteCommandKind.Play:
                PlayFromHere();
                break;

            case RemoteCommandKind.Pause:
                if (_sync.IsPlaying) _sync.Pause();
                break;

            case RemoteCommandKind.Stop:
                if (_sync.IsPlaying) _sync.Pause();
                SeekFrame(_sync.SegmentInFrame);
                break;

            case RemoteCommandKind.Rewind:
                SeekFrame(_sync.SegmentInFrame);
                break;

            // Тот же StepBy, что у стрелок и кнопок транспорта: шаг сам снимает
            // воспроизведение с паузой, так что Step на игре останавливает видео.
            case RemoteCommandKind.Step:
                StepBy(command.Flag ? 1 : -1);
                break;

            case RemoteCommandKind.Loop when command.Flag != _loop:
                ToggleLoop();
                break;
        }

        Status($"Unity: {command.Describe()}");
    }
}
