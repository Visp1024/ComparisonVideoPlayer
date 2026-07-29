using System.IO;
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
        // На время задачи #9 сервер поднимается всегда; тумблер появится следующей задачей.
        _ = StartRemote(App.Settings.RemotePipeName);
    }

    /// <summary>Поднять сервер; false — имя канала занято другим процессом.</summary>
    private bool StartRemote(string pipeName)
    {
        StopRemote();

        var server = new PipeServer(pipeName, _remoteLog);
        server.CommandReceived += (_, command) => Dispatcher.BeginInvoke(() => ApplyRemote(command));

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
            _remoteLog.Add($"не удалось открыть канал «{pipeName}»: имя занято");
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
            _remoteLog.Add("пропущено: файл не открыт");
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

            case RemoteCommandKind.Loop when command.Flag != _loop:
                ToggleLoop();
                break;
        }

        Status($"Unity: {command.Describe()}");
    }
}
