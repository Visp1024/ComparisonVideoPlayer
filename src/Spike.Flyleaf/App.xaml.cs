using System.IO;
using System.Windows;
using FlyleafLib;

namespace Spike.Flyleaf;

/// <summary>Точка входа спайка: поднимает движок FlyleafLib и открывает окно с двумя плеерами.</summary>
public partial class App : Application
{
    /// <summary>Запуск с ключом --bench: автопрогон замеров и выход.</summary>
    public static bool AutoBench { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        AutoBench = e.Args.Contains("--bench");
        Directory.CreateDirectory(SpikeEnv.OutDir);

        Engine.Start(new EngineConfig
        {
            FFmpegPath = SpikeEnv.FFmpegDir,
            UIRefresh = true,
            UIRefreshInterval = 100,
            LogOutput = Path.Combine(SpikeEnv.OutDir, "flyleaf.log"),
            LogLevel = LogLevel.Info
        });

        base.OnStartup(e);
    }
}
