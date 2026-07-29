using System;

#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
#endif

namespace Cvp
{
    /// <summary>
    /// Соединение с плеером CVP по именованному каналу. Всё общение уходит в фоновый
    /// поток: вызов из игрового кода обязан возвращаться мгновенно и не бросать, даже
    /// когда плеер не запущен.
    /// </summary>
    public sealed class CvpPipeClient : IDisposable
    {
        /// <summary>
        /// Предел очереди. Триггер в цикле при выключенном плеере не должен
        /// съедать память: лишние команды отбрасываются, а не копятся.
        /// </summary>
        public const int QueueLimit = 64;

        /// <summary>Пауза между попытками подключения, мс.</summary>
        private const int RetryDelayMs = 2000;

        /// <summary>
        /// Как часто на простое отправлять пустую строку, мс. Это же — задержка,
        /// с которой Unity узнаёт, что плеер закрылся.
        /// </summary>
        private const int KeepAliveMs = 500;

#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
        private readonly string _pipeName;
        private readonly Queue<string> _queue = new Queue<string>();
        private readonly object _gate = new object();
        private readonly Thread _thread;
        private volatile bool _running = true;
        private volatile bool _connected;
#endif

        public CvpPipeClient(string pipeName)
        {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            _pipeName = pipeName;
            _thread = new Thread(Loop) { IsBackground = true, Name = "CVP Link" };
            _thread.Start();
#endif
        }

        /// <summary>Держит ли клиент живое соединение с плеером.</summary>
        public bool IsConnected
        {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            get { return _connected; }
#else
            get { return false; }
#endif
        }

        /// <summary>Поставить строку протокола в очередь. Не блокирует и не бросает.</summary>
        public void Send(string json)
        {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            lock (_gate)
            {
                if (_queue.Count >= QueueLimit) _queue.Dequeue();
                _queue.Enqueue(json);
                Monitor.Pulse(_gate);
            }
#endif
        }

        public void Dispose()
        {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            _running = false;
            lock (_gate) Monitor.Pulse(_gate);
            _thread.Join(500);
#endif
        }

#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
        private void Loop()
        {
            while (_running)
            {
                NamedPipeClientStream pipe = null;

                try
                {
                    pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out);

                    // Короткий таймаут вместо бесконечного ожидания: поток обязан
                    // регулярно проверять _running, иначе Unity не закроется.
                    pipe.Connect(500);
                    _connected = true;
                    Pump(pipe);
                }
                catch (Exception)
                {
                    // Плеера нет, канал закрыт, соединение оборвалось — всё это
                    // штатно: ждём и пробуем снова.
                }
                finally
                {
                    _connected = false;
                    if (pipe != null) pipe.Dispose();
                }

                if (_running) Thread.Sleep(RetryDelayMs);
            }
        }

        /// <summary>Отдавать команды, пока соединение живо. Выходит исключением при разрыве.</summary>
        private void Pump(NamedPipeClientStream pipe)
        {
            while (_running)
            {
                string json;

                lock (_gate)
                {
                    // Ждём ровно один раз, а не в цикле: по истечении таймаута нужно
                    // выйти отсюда и всё-таки записать в канал — на простое это
                    // единственный способ заметить, что плеер закрылся.
                    if (_queue.Count == 0) Monitor.Wait(_gate, KeepAliveMs);
                    if (!_running) return;

                    // Пустая строка вместо проверки IsConnected: у клиентского конца
                    // канала он остаётся true и после того, как плеер закрылся, —
                    // разрыв виден только на записи. Плеер такую строку игнорирует.
                    json = _queue.Count == 0 ? string.Empty : _queue.Dequeue();
                }

                var bytes = Encoding.UTF8.GetBytes(json + "\n");
                pipe.Write(bytes, 0, bytes.Length);
                pipe.Flush();
            }
        }
#endif
    }
}
