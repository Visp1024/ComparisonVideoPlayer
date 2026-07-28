using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace Spike.Mpv;

/// <summary>Точка входа спайка: находит libmpv-2.dll и открывает окно с двумя плеерами.</summary>
public partial class App : Application
{
    /// <summary>Запуск с ключом --bench: автопрогон замеров и выход.</summary>
    public static bool AutoBench { get; private set; }

    /// <summary>Ключ --open &lt;файл&gt;: открыть ролик сразу в обоих плеерах (проверка рендера в окно).</summary>
    public static string? OpenPath { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        AutoBench = e.Args.Contains("--bench");

        var openIdx = Array.IndexOf(e.Args, "--open");
        if (openIdx >= 0 && openIdx + 1 < e.Args.Length) OpenPath = e.Args[openIdx + 1];
        Directory.CreateDirectory(SpikeEnv.OutDir);

        // libmpv в репозиторий не кладётся (объём) — DLL грузится из папки, заданной SPIKE_MPV_DIR
        if (!File.Exists(SpikeEnv.MpvDll))
            throw new FileNotFoundException($"не найдена libmpv-2.dll: {SpikeEnv.MpvDll} (задайте SPIKE_MPV_DIR)");
        if (!SetDllDirectory(SpikeEnv.MpvDir))
            throw new InvalidOperationException($"SetDllDirectory({SpikeEnv.MpvDir}) не сработал: {Marshal.GetLastWin32Error()}");

        base.OnStartup(e);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectory(string path);
}
