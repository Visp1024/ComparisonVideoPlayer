using UnityEditor;
using UnityEngine;

namespace Cvp.Editor
{
    /// <summary>
    /// Пульт CVP в редакторе: состояние связи и команды транспорта без входа в Play Mode.
    /// Нужен и как инструмент (аниматор гоняет отрезок кнопкой), и как способ проверить
    /// связь, не дописывая тестовый скрипт.
    /// </summary>
    public sealed class CvpLinkWindow : EditorWindow
    {
        private string _pipeName = "cvp";

        [MenuItem("Window/CVP Link")]
        private static void Open()
        {
            GetWindow<CvpLinkWindow>("CVP Link").minSize = new Vector2(260, 250);
        }

        private void OnEnable()
        {
            _pipeName = CvpPlayer.PipeName;

            // Окно должно показывать связь живой, а не на момент открытия: перерисовка
            // по таймеру дешевле, чем подписки через границу потоков.
            EditorApplication.update += Repaint;
        }

        private void OnDisable()
        {
            EditorApplication.update -= Repaint;
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(6);

            var connected = CvpPlayer.IsConnected;
            EditorGUILayout.HelpBox(
                connected ? "Плеер подключён." : "Плеера нет. Запустите CVP и включите внешнее управление.",
                connected ? MessageType.Info : MessageType.Warning);

            EditorGUI.BeginChangeCheck();
            _pipeName = EditorGUILayout.TextField("Имя канала", _pipeName);
            if (EditorGUI.EndChangeCheck()) CvpPlayer.PipeName = _pipeName;

            EditorGUI.BeginChangeCheck();
            var follow = EditorGUILayout.ToggleLeft(
                new GUIContent(
                    "Следовать за паузой и Step",
                    "Пауза редактора ставит на паузу и видео, Step переводит его на кадр."),
                CvpEditorPause.Enabled);
            if (EditorGUI.EndChangeCheck()) CvpEditorPause.Enabled = follow;

            EditorGUILayout.Space(8);

            using (new EditorGUI.DisabledScope(!connected))
            {
                if (GUILayout.Button("Играть с начала отрезка")) CvpPlayer.Play();

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Пауза")) CvpPlayer.Pause();
                    if (GUILayout.Button("Стоп")) CvpPlayer.Stop();
                    if (GUILayout.Button("В начало")) CvpPlayer.Rewind();
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("◀ Кадр")) CvpPlayer.Step(false);
                    if (GUILayout.Button("Кадр ▶")) CvpPlayer.Step();
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Петля вкл")) CvpPlayer.SetLoop(true);
                    if (GUILayout.Button("Петля выкл")) CvpPlayer.SetLoop(false);
                }
            }
        }
    }
}
