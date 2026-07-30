using UnityEngine;

namespace Cvp
{
    /// <summary>
    /// Управление плеером CVP из любого места кода. Отрезок, файлы и выравнивание
    /// треков задаются в самом плеере — отсюда идут только команды транспорта.
    /// </summary>
    /// <example>
    /// <code>
    /// CvpPlayer.Play();                 // показать отрезок с начала
    /// CvpPlayer.Play(fromStart: false); // продолжить с текущей позиции
    /// </code>
    /// </example>
    public static class CvpPlayer
    {
        private static CvpPipeClient _client;
        private static string _pipeName = "cvp";
        private static readonly object Gate = new object();

        /// <summary>
        /// Имя канала, как в настройках плеера. Менять до первого вызова команды:
        /// смена уже поднятого соединения переподключает его.
        /// </summary>
        public static string PipeName
        {
            get { return _pipeName; }
            set
            {
                lock (Gate)
                {
                    if (_pipeName == value) return;
                    _pipeName = value;

                    if (_client == null) return;
                    _client.Dispose();
                    _client = null;
                }
            }
        }

        /// <summary>Есть ли живое соединение с плеером. Соединение поднимается лениво.</summary>
        public static bool IsConnected
        {
            get { return Client.IsConnected; }
        }

        /// <param name="fromStart">
        /// true (по умолчанию) — встать на начало отрезка и играть; false — продолжить
        /// с текущей позиции.
        /// </param>
        public static void Play(bool fromStart = true)
        {
            Send("{\"cmd\":\"play\",\"fromStart\":" + (fromStart ? "true" : "false") + "}");
        }

        /// <summary>Пауза на месте.</summary>
        public static void Pause() { Send("{\"cmd\":\"pause\"}"); }

        /// <summary>Пауза и возврат на начало отрезка.</summary>
        public static void Stop() { Send("{\"cmd\":\"stop\"}"); }

        /// <summary>Встать на начало отрезка, не запуская воспроизведение.</summary>
        public static void Rewind() { Send("{\"cmd\":\"rewind\"}"); }

        /// <summary>
        /// Шаг ровно на один кадр. Как и в самом плеере, шаг снимает воспроизведение:
        /// после команды плеер стоит на паузе на новом кадре.
        /// </summary>
        /// <param name="forward">true (по умолчанию) — вперёд, false — назад.</param>
        public static void Step(bool forward = true)
        {
            Send("{\"cmd\":\"step\",\"forward\":" + (forward ? "true" : "false") + "}");
        }

        /// <summary>Повторять отрезок по кругу.</summary>
        public static void SetLoop(bool on)
        {
            Send("{\"cmd\":\"loop\",\"on\":" + (on ? "true" : "false") + "}");
        }

        /// <summary>Закрыть соединение. Вызывать не обязательно — поток фоновый.</summary>
        public static void Disconnect()
        {
            lock (Gate)
            {
                if (_client == null) return;
                _client.Dispose();
                _client = null;
            }
        }

        private static CvpPipeClient Client
        {
            get
            {
                lock (Gate)
                {
                    if (_client == null) _client = new CvpPipeClient(_pipeName);
                    return _client;
                }
            }
        }

        private static void Send(string json)
        {
            Client.Send(json);
        }

#if UNITY_EDITOR
        // В редакторе домен перезагружается на каждой компиляции: не закрыв поток,
        // получаем висящее соединение и занятый слот на стороне плеера.
        [UnityEditor.InitializeOnLoadMethod]
        private static void HookEditorReload()
        {
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += Disconnect;
            UnityEditor.EditorApplication.quitting += Disconnect;
        }
#endif

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnPlay()
        {
            // Domain reload может быть отключён в настройках проекта — тогда статика
            // переживает выход из Play Mode, и соединение надо поднимать заново.
            Disconnect();
        }
    }
}
