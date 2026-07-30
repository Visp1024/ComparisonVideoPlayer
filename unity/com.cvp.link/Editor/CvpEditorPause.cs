using UnityEditor;
using UnityEngine;

namespace Cvp.Editor
{
    /// <summary>
    /// Мост «пауза и Step редактора → плеер»: Unity встал на паузу — встаёт и видео,
    /// нажали Step — видео переходит на кадр. Нужен, чтобы сравнивать кадр движка с
    /// эталоном в плеере, не трогая транспорт руками.
    /// </summary>
    /// <remarks>
    /// Выключено по умолчанию: проект может гонять отрезок сам (из кода или окна
    /// пульта), и тогда вмешательство редактора только мешает.
    /// </remarks>
    [InitializeOnLoad]
    public static class CvpEditorPause
    {
        private const string PrefKey = "Cvp.Link.FollowEditorPause";

        /// <summary>
        /// Предел шагов за один тик. Кадры набегают не только от Step (перекомпиляция,
        /// переход в Play Mode), и превращать такой всплеск в промотку видео незачем.
        /// </summary>
        private const int MaxStepsPerTick = 4;

        private static int _frames;
        private static bool _pausedByUs;

        static CvpEditorPause()
        {
            EditorApplication.update += OnUpdate;
            EditorApplication.pauseStateChanged += OnPauseStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        /// <summary>
        /// Следить ли за паузой и шагом редактора. Настройка машины, а не проекта:
        /// связь с плеером — свойство рабочего места.
        /// </summary>
        public static bool Enabled
        {
            get { return EditorPrefs.GetBool(PrefKey, false); }
            set { EditorPrefs.SetBool(PrefKey, value); }
        }

        private static void OnUpdate()
        {
            if (!EditorApplication.isPlaying || !EditorApplication.isPaused)
            {
                // Счётчик кадров вне паузы бежит сам — запоминаем его, чтобы вход в
                // паузу не выглядел пачкой шагов.
                _frames = Time.frameCount;
                return;
            }

            var advanced = Time.frameCount - _frames;
            _frames = Time.frameCount;

            // На паузе игровой цикл стоит: новые кадры берутся только от кнопки Step.
            // События для неё редактор не даёт, а счётчик кадров — даёт.
            if (advanced <= 0 || !Enabled) return;

            var steps = Mathf.Min(advanced, MaxStepsPerTick);
            for (var i = 0; i < steps; i++) CvpPlayer.Step();
        }

        private static void OnPauseStateChanged(PauseState state)
        {
            if (!EditorApplication.isPlaying) return;

            _frames = Time.frameCount;
            if (!Enabled) return;

            if (state == PauseState.Paused)
            {
                // «Только если идёт воспроизведение» решает плеер: связь односторонняя,
                // состояния воспроизведения здесь нет, а пауза на паузе — не действие.
                CvpPlayer.Pause();
                _pausedByUs = true;
                return;
            }

            // Продолжаем только то, что сами и остановили: включённая на уже
            // остановленном редакторе опция не должна запускать чужое видео.
            if (!_pausedByUs) return;

            _pausedByUs = false;
            CvpPlayer.Play(false);
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            // Выход из Play Mode — это не «сняли паузу»: видео остаётся там, где встало.
            if (change == PlayModeStateChange.ExitingPlayMode) _pausedByUs = false;

            _frames = Time.frameCount;
        }
    }
}
