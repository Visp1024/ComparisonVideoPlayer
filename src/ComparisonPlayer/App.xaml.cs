using System.IO;
using System.Windows;
using ComparisonPlayer.Chrome;
using FlyleafLib;

namespace ComparisonPlayer;

/// <summary>
/// Точка входа: поднимает движок FlyleafLib до создания окна —
/// без запущенного Engine ни один Player не создаётся.
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// Действующие настройки. Окно настроек (фаза 5) правит копию и по «Сохранить»
    /// подменяет её целиком, поэтому свойство пишется не только отсюда.
    /// </summary>
    public static Settings Settings { get; set; } = new();

    /// <summary>Файл из командной строки: запуск через «Открыть с помощью» или из консоли.</summary>
    public static string? StartupFile { get; private set; }

    /// <summary>
    /// Второй файл из командной строки — открывается в трек B. Сравнение обычно
    /// начинают сразу с пары роликов, и заставлять открывать второй вручную незачем.
    /// </summary>
    public static string? StartupFileB { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        var files = e.Args.Where(a => !a.StartsWith('-')).ToList();
        StartupFile = files.FirstOrDefault();
        StartupFileB = files.Skip(1).FirstOrDefault();
        Settings = Settings.Load();
        Directory.CreateDirectory(AppEnv.DataDir);
        AppEnv.CleanupEngineLogs();

        // Библиотек нет — предлагаем скачать их до старта движка. Иначе Engine.Start
        // упадёт и единственным выходом останется идти за сборкой FFmpeg руками.
        // Отказ ничего не ломает: запуск продолжится прежним путём, с прежней ошибкой.
        if (!AppEnv.FFmpegLooksUsable)
        {
            // Пока главного окна нет, закрытие диалога при OnLastWindowClose означало бы
            // «закрылось последнее окно» и погасило бы приложение целиком.
            var shutdownMode = ShutdownMode;
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            new FFmpegSetupWindow().ShowDialog();
            ShutdownMode = shutdownMode;
        }

        try
        {
            Engine.Start(new EngineConfig
            {
                FFmpegPath = AppEnv.FFmpegDir,
                UIRefresh = true,
                UIRefreshInterval = 100,   // с этим интервалом обновляется CurTime при воспроизведении
                LogOutput = AppEnv.EngineLogFile,
                LogLevel = LogLevel.Info
            });
        }
        catch (Exception ex)
        {
            // Подсказку про FFmpeg показываем, только если каталог библиотек и правда негоден:
            // раньше она стояла в любом отказе и уводила от настоящей причины.
            var message = $"Не удалось запустить движок воспроизведения.\n\n{ex.Message}";
            string? detail = null;
            if (!AppEnv.FFmpegLooksUsable)
            {
                message +=
                    "\n\nУкажите каталог библиотек FFmpeg переменной окружения COMPARISONPLAYER_FFMPEG_DIR " +
                    "или положите их в подкаталог FFmpeg рядом с программой.\n\n" +
                    "Предложение скачать готовый комплект появится снова при следующем запуске.";
                detail = "Библиотеки ожидались в каталоге: " +
                         (string.IsNullOrEmpty(AppEnv.FFmpegDir) ? "(не найден)" : AppEnv.FFmpegDir);
            }

            MessageDialog.Show(null, "CVP", message, detail);
            Shutdown(1);
            return;
        }

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Settings.Save();
        base.OnExit(e);
    }
}
