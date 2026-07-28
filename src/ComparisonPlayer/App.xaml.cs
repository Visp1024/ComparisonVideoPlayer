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
    public static Settings Settings { get; private set; } = new();

    /// <summary>Файл из командной строки: запуск через «Открыть с помощью» или из консоли.</summary>
    public static string? StartupFile { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        StartupFile = e.Args.FirstOrDefault(a => !a.StartsWith('-'));
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
