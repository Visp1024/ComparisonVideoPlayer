using System.IO;
using System.Windows;
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
            MessageBox.Show(
                $"Не удалось запустить движок воспроизведения.\n\n{ex.Message}\n\n" +
                $"Библиотеки FFmpeg ожидались в каталоге:\n{(string.IsNullOrEmpty(AppEnv.FFmpegDir) ? "(не найден)" : AppEnv.FFmpegDir)}\n\n" +
                "Укажите каталог переменной окружения COMPARISONPLAYER_FFMPEG_DIR " +
                "или положите библиотеки в подкаталог FFmpeg рядом с программой.",
                "ComparisonVideoPlayer", MessageBoxButton.OK, MessageBoxImage.Error);
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
