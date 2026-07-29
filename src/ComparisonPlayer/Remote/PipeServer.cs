using System.IO;
using System.IO.Pipes;
using System.Text;

namespace ComparisonPlayer.Remote;

/// <summary>
/// Сервер внешнего управления на именованном канале Windows. Держит несколько
/// одновременных подключений: Unity Editor и собранный билд часто работают рядом,
/// и заставлять их драться за единственный слот незачем.
/// </summary>
public sealed class PipeServer : IDisposable
{
    /// <summary>Сколько клиентов пускаем разом. Больше на практике не нужно.</summary>
    public const int MaxClients = 4;

    private readonly string _pipeName;
    private readonly RemoteLog _log;
    private readonly Lock _gate = new();

    private CancellationTokenSource? _cts;
    private int _clients;

    public PipeServer(string pipeName, RemoteLog log)
    {
        _pipeName = pipeName;
        _log = log;
    }

    /// <summary>Сколько клиентов подключено прямо сейчас.</summary>
    public int ClientCount { get { lock (_gate) return _clients; } }

    /// <summary>Пришла команда. Поднимается из фонового потока.</summary>
    public event EventHandler<RemoteCommand>? CommandReceived;

    /// <summary>Число подключённых изменилось. Поднимается из фонового потока.</summary>
    public event EventHandler? ClientsChanged;

    /// <summary>
    /// Поднять канал. Бросает <see cref="IOException"/>, если имя занято другим
    /// процессом: это ошибка, которую пользователь должен увидеть, а не проглотить.
    /// </summary>
    public void Start()
    {
        if (_cts is not null) return;

        // Первый экземпляр создаём синхронно: только так «имя занято» превращается
        // в исключение здесь, а не в тихий сбой фонового потока.
        var probe = CreateStream();
        _cts = new CancellationTokenSource();

        var token = _cts.Token;
        _ = Task.Run(() => AcceptLoop(probe, token), token);

        for (var i = 1; i < MaxClients; i++)
            _ = Task.Run(() => AcceptLoop(null, token), token);

        _log.Add($"канал \\\\.\\pipe\\{_pipeName} открыт");
    }

    public void Stop()
    {
        if (_cts is null) return;

        _cts.Cancel();
        _cts.Dispose();
        _cts = null;

        lock (_gate) _clients = 0;
        ClientsChanged?.Invoke(this, EventArgs.Empty);
        _log.Add("канал закрыт");
    }

    public void Dispose() => Stop();

    private NamedPipeServerStream CreateStream() =>
        new(_pipeName, PipeDirection.In, MaxClients,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

    /// <summary>
    /// Один слот подключения: ждём клиента, читаем его строки, при разрыве ждём
    /// следующего. Слот живёт, пока сервер не остановлен.
    /// </summary>
    private async Task AcceptLoop(NamedPipeServerStream? first, CancellationToken token)
    {
        var stream = first;

        while (!token.IsCancellationRequested)
        {
            stream ??= CreateStream();
            var counted = false;

            try
            {
                await stream.WaitForConnectionAsync(token).ConfigureAwait(false);

                counted = true;
                Changed(+1);
                _log.Add("клиент подключился");

                await ReadLines(stream, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Остановка сервера — штатный выход из цикла.
            }
            catch (IOException)
            {
                // Клиент оборвал соединение: слот просто переоткрывается.
            }
            finally
            {
                // Свой флаг, а не IsConnected: к этому моменту канал уже разорван,
                // и по нему отключение выглядело бы как «клиента и не было».
                if (counted)
                {
                    Changed(-1);
                    _log.Add("клиент отключился");
                }

                await stream.DisposeAsync().ConfigureAwait(false);
                stream = null;
            }
        }
    }

    private async Task ReadLines(NamedPipeServerStream stream, CancellationToken token)
    {
        // Свой ридер вместо StreamReader: тот при разрыве канала ведёт себя
        // непредсказуемо на частичной строке, а нам нужна ровно построчная граница.
        var buffer = new byte[4096];
        var chars = new char[4096];
        var line = new StringBuilder();

        // Decoder, а не Encoding.GetString на каждое чтение: кириллица в чужой
        // команде может лечь на границу двух чтений, и декодирование без состояния
        // превратило бы её в «ромбики».
        var decoder = Encoding.UTF8.GetDecoder();

        while (!token.IsCancellationRequested)
        {
            var read = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
            if (read == 0) return;

            var count = decoder.GetChars(buffer, 0, read, chars, 0);

            for (var i = 0; i < count; i++)
            {
                if (chars[i] is '\n')
                {
                    Handle(line.ToString().Trim('\r'));
                    line.Clear();
                }
                else if (line.Length < 8192)
                {
                    line.Append(chars[i]);
                }
                // Строка длиннее 8 КБ — не наш протокол; молча режем, чтобы чужой
                // процесс не мог заставить нас копить память.
            }
        }
    }

    private void Handle(string line)
    {
        // Пустая строка — keep-alive клиента: односторонний канал иначе не замечает,
        // что плеер закрылся, и Unity продолжает считать себя подключённым. Молча:
        // засорять журнал раз в полсекунды нельзя.
        if (string.IsNullOrWhiteSpace(line)) return;

        if (RemoteCommand.TryParse(line, out var command, out var error))
        {
            _log.Add(command!.Describe());
            CommandReceived?.Invoke(this, command);
        }
        else
        {
            _log.Add($"отказ: {error}");
        }
    }

    private void Changed(int delta)
    {
        lock (_gate) _clients = Math.Max(0, _clients + delta);
        ClientsChanged?.Invoke(this, EventArgs.Empty);
    }
}
