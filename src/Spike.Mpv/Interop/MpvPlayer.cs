using System.Runtime.InteropServices;

namespace Spike.Mpv.Interop;

/// <summary>
/// Обёртка над одним экземпляром libmpv: рендер в переданный HWND, транспортные команды,
/// чтение свойств и счётчики «плеер показал новый кадр» для замеров.
///
/// Позиция считается изменившейся, когда libmpv прислал property-change по time-pos
/// с новым значением; отдельно считаются события playback-restart (libmpv шлёт его, когда
/// после seek готов первый кадр). Оба счётчика нужны замерам: какой из сигналов приходит
/// на каждую операцию — вопрос эмпирический, см. диагностический проход в BenchRunner.
/// </summary>
public sealed class MpvPlayer : IDisposable
{
    private readonly IntPtr _ctx;
    private readonly Thread _eventThread;
    private readonly Action<string>? _log;
    private volatile bool _running = true;

    private double _lastPos = double.NaN;
    private long _posGeneration;
    private long _playbackRestarts;
    private long _fileLoaded;
    private readonly List<string> _eventTrace = [];
    private bool _traceEvents;

    public string Name { get; }

    public MpvPlayer(string name, IntPtr hwnd, Action<string>? log = null)
    {
        Name = name;
        _log = log;

        _ctx = MpvNative.mpv_create();
        if (_ctx == IntPtr.Zero) throw new InvalidOperationException("mpv_create вернул null (libmpv-2.dll не загрузилась?)");

        // Рендер в дочернее окно WPF. wid обязан быть выставлен ДО mpv_initialize.
        long wid = hwnd.ToInt64();
        Check(MpvNative.mpv_set_option(_ctx, MpvNative.U8("wid"), (int)MpvFormat.Int64, ref wid), "wid");

        // Конфигурация под покадровую работу — аналог настроек спайка FlyleafLib.
        SetOption("terminal", "no");
        SetOption("config", "no");             // не подхватывать mpv.conf пользователя
        SetOption("input-default-bindings", "no");
        SetOption("input-vo-keyboard", "no");
        SetOption("osc", "no");
        SetOption("osd-level", "0");
        SetOption("audio", "no");              // на этапе спайка звук не нужен
        SetOption("pause", "yes");
        SetOption("keep-open", "always");      // не закрывать файл на последнем кадре
        SetOption("hr-seek", "yes");           // точный seek — требование проекта
        SetOption("hr-seek-framedrop", "no");
        SetOption("vo", "gpu");
        SetOption("hwdec", "d3d11va");         // аппаратный декод, как в спайке Flyleaf
        SetOption("video-sync", "audio");
        SetOption("untimed", "no");

        Check(MpvNative.mpv_initialize(_ctx), "mpv_initialize");
        Check(MpvNative.mpv_request_log_messages(_ctx, MpvNative.U8("warn")), "request_log_messages");
        Check(MpvNative.mpv_observe_property(_ctx, 1, MpvNative.U8("time-pos"), (int)MpvFormat.Double), "observe time-pos");

        _eventThread = new Thread(EventLoop) { IsBackground = true, Name = $"mpv-events-{name}" };
        _eventThread.Start();
    }

    public static string ApiVersion
    {
        get
        {
            var v = MpvNative.mpv_client_api_version().ToInt64();
            return $"{(v >> 16) & 0xFFFF}.{v & 0xFFFF}";
        }
    }

    /// <summary>Сколько раз libmpv сообщил о новой позиции (то есть о новом показанном кадре).</summary>
    public long PositionGeneration => Interlocked.Read(ref _posGeneration);

    /// <summary>Сколько раз пришло playback-restart (после seek — когда готов первый кадр).</summary>
    public long PlaybackRestarts => Interlocked.Read(ref _playbackRestarts);

    public double Position => Volatile.Read(ref _lastPos);

    /// <summary>
    /// pts кадра, который VO показывает прямо сейчас. В отличие от time-pos (libmpv выставляет
    /// его сразу по команде seek, до декода) это состояние картинки, а не намерения плеера —
    /// именно по нему замеряется «кадр показан». NaN, если сборка libmpv свойства не отдаёт.
    /// </summary>
    public double DisplayedFramePts => GetDouble("video-frame-info/pts");

    /// <summary>Есть ли в этой сборке libmpv честный сигнал о показанном кадре.</summary>
    public bool HasDisplayedFramePts => !double.IsNaN(DisplayedFramePts);

    // ---- команды и свойства -------------------------------------------------

    public void SetOption(string name, string value)
        => Check(MpvNative.mpv_set_option_string(_ctx, MpvNative.U8(name), MpvNative.U8(value)), $"опция {name}={value}");

    public void SetProperty(string name, string value)
        => Check(MpvNative.mpv_set_property_string(_ctx, MpvNative.U8(name), MpvNative.U8(value)), $"свойство {name}={value}");

    /// <summary>Синхронная команда mpv (возвращается после выполнения в ядре плеера).</summary>
    public void Command(params string[] args)
    {
        var code = CommandRaw(args);
        if (code < 0) throw new MpvException($"команда [{string.Join(' ', args)}] не выполнилась: {MpvNative.ErrorText(code)}");
    }

    /// <summary>Команда без исключения — для случаев, когда ошибка сама по себе результат замера.</summary>
    public int CommandRaw(params string[] args)
    {
        var ptrs = new IntPtr[args.Length + 1];
        try
        {
            for (int i = 0; i < args.Length; i++)
            {
                var bytes = MpvNative.U8(args[i]);
                ptrs[i] = Marshal.AllocHGlobal(bytes.Length);
                Marshal.Copy(bytes, 0, ptrs[i], bytes.Length);
            }
            ptrs[^1] = IntPtr.Zero;
            return MpvNative.mpv_command(_ctx, ptrs);
        }
        finally
        {
            foreach (var p in ptrs)
                if (p != IntPtr.Zero) Marshal.FreeHGlobal(p);
        }
    }

    public double GetDouble(string name)
        => MpvNative.mpv_get_property_double(_ctx, MpvNative.U8(name), (int)MpvFormat.Double, out var v) < 0 ? double.NaN : v;

    public long GetLong(string name, long fallback = -1)
        => MpvNative.mpv_get_property_long(_ctx, MpvNative.U8(name), (int)MpvFormat.Int64, out var v) < 0 ? fallback : v;

    public string? GetString(string name)
    {
        if (MpvNative.mpv_get_property_ptr(_ctx, MpvNative.U8(name), (int)MpvFormat.String, out var p) < 0 || p == IntPtr.Zero)
            return null;
        try { return MpvNative.Utf8(p); }
        finally { MpvNative.mpv_free(p); }
    }

    /// <summary>Открывает файл и ждёт первого показанного кадра. Возвращает время открытия, мс.</summary>
    public double LoadFile(string path, int timeoutMs = 30_000)
    {
        var loaded0 = Interlocked.Read(ref _fileLoaded);
        var restarts0 = PlaybackRestarts;
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();

        Command("loadfile", path, "replace");

        while (Interlocked.Read(ref _fileLoaded) == loaded0 || PlaybackRestarts == restarts0)
        {
            if (System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds > timeoutMs)
                throw new MpvException($"файл не открылся за {timeoutMs} мс: {path}");
            Thread.Sleep(2);
        }
        return System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
    }

    /// <summary>
    /// Ждёт, пока плеер перестанет присылать события (тишина idleMs подряд). Обязательный шаг
    /// перед замером: libmpv досылает playback-restart и смену time-pos уже после того, как
    /// предыдущая операция считается завершённой, и без «успокоения» эти хвосты засчитываются
    /// следующему замеру — время получается заниженным, а снимок берётся от прошлого кадра.
    /// </summary>
    public void Quiesce(int idleMs = 60, int timeoutMs = 5_000)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var gen = PositionGeneration;
        var restarts = PlaybackRestarts;
        var quietFrom = System.Diagnostics.Stopwatch.GetTimestamp();

        while (System.Diagnostics.Stopwatch.GetElapsedTime(quietFrom).TotalMilliseconds < idleMs)
        {
            if (PositionGeneration != gen || PlaybackRestarts != restarts)
            {
                gen = PositionGeneration;
                restarts = PlaybackRestarts;
                quietFrom = System.Diagnostics.Stopwatch.GetTimestamp();
            }
            if (System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds > timeoutMs) return;
            Thread.Sleep(2);
        }
    }

    // ---- диагностика последовательности событий -----------------------------

    public void BeginEventTrace()
    {
        lock (_eventTrace) { _eventTrace.Clear(); _traceEvents = true; }
    }

    public IReadOnlyList<string> EndEventTrace()
    {
        lock (_eventTrace) { _traceEvents = false; return _eventTrace.ToArray(); }
    }

    // ---- цикл событий -------------------------------------------------------

    private void EventLoop()
    {
        while (_running)
        {
            var evPtr = MpvNative.mpv_wait_event(_ctx, 0.05);
            if (evPtr == IntPtr.Zero) continue;

            var ev = Marshal.PtrToStructure<MpvEvent>(evPtr);
            switch (ev.EventId)
            {
                case MpvEventId.None:
                    continue;

                case MpvEventId.Shutdown:
                    _running = false;
                    break;

                case MpvEventId.FileLoaded:
                    Interlocked.Increment(ref _fileLoaded);
                    break;

                case MpvEventId.PlaybackRestart:
                    Interlocked.Increment(ref _playbackRestarts);
                    break;

                case MpvEventId.PropertyChange:
                    HandlePropertyChange(ev);
                    break;

                case MpvEventId.LogMessage:
                    var msg = Marshal.PtrToStructure<MpvEventLogMessage>(ev.Data);
                    _log?.Invoke($"[{Name}/mpv] {MpvNative.Utf8(msg.Prefix)}: {MpvNative.Utf8(msg.Text)?.TrimEnd()}");
                    break;
            }

            Trace(ev);
        }
    }

    private void HandlePropertyChange(MpvEvent ev)
    {
        if (ev.Data == IntPtr.Zero) return;
        var prop = Marshal.PtrToStructure<MpvEventProperty>(ev.Data);
        if (prop.Format != MpvFormat.Double || prop.Data == IntPtr.Zero) return;

        var value = Marshal.PtrToStructure<double>(prop.Data);
        var prev = Volatile.Read(ref _lastPos);
        if (value.Equals(prev)) return;

        Volatile.Write(ref _lastPos, value);
        Interlocked.Increment(ref _posGeneration);
    }

    private void Trace(MpvEvent ev)
    {
        if (!_traceEvents) return;
        lock (_eventTrace)
        {
            if (!_traceEvents) return;
            var extra = ev.EventId == MpvEventId.PropertyChange ? $" (time-pos={Position:F4})" : "";
            _eventTrace.Add($"{ev.EventId}{extra}");
        }
    }

    private static void Check(int code, string what)
    {
        if (code < 0) throw new MpvException($"{what}: {MpvNative.ErrorText(code)}");
    }

    public void Dispose()
    {
        _running = false;
        MpvNative.mpv_wakeup(_ctx);
        _eventThread.Join(2000);
        MpvNative.mpv_terminate_destroy(_ctx);
    }
}

public sealed class MpvException(string message) : Exception(message);
