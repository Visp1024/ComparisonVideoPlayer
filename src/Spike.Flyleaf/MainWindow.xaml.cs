using System.IO;
using System.Windows;
using System.Windows.Input;
using FlyleafLib;
using FlyleafLib.MediaPlayer;
using Microsoft.Win32;
using Spike.Flyleaf.Bench;

namespace Spike.Flyleaf;

/// <summary>
/// Окно спайка: два независимых Player рядом, ручной покадровый шаг с клавиатуры
/// и кнопка автоматического прогона замеров (та же логика, что при запуске с --bench).
/// </summary>
public partial class MainWindow : Window
{
    public Player PlayerA { get; }
    public Player PlayerB { get; }

    public MainWindow()
    {
        InitializeComponent();

        PlayerA = new Player(MakeConfig());
        PlayerB = new Player(MakeConfig());

        Loaded += OnLoaded;
        KeyDown += OnKeyDown;
    }

    private static Config MakeConfig()
    {
        var cfg = new Config();
        cfg.Player.AutoPlay = false;
        cfg.Player.SeekAccurate = true;      // точный seek — обязательное требование проекта
        cfg.Audio.Enabled = false;           // на этапе спайка звук не нужен
        cfg.Video.VideoAcceleration = true;  // аппаратный декод D3D11
        return cfg;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        HostA.Player = PlayerA;
        HostB.Player = PlayerB;

        Log($"FFmpeg: {SpikeEnv.FFmpegDir}");
        Log($"тестовые ролики: {SpikeEnv.MediaDir}");
        Log($"результаты: {SpikeEnv.OutDir}");

        if (App.AutoBench)
        {
            await RunBenchAsync();
            Application.Current.Shutdown();
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Left: StepPrev_Click(sender, e); break;
            case Key.Right: StepNext_Click(sender, e); break;
            case Key.Space: PlayPause_Click(sender, e); break;
        }
    }

    private void OpenA_Click(object sender, RoutedEventArgs e) => OpenInto(PlayerA);
    private void OpenB_Click(object sender, RoutedEventArgs e) => OpenInto(PlayerB);

    private void OpenInto(Player p)
    {
        var dlg = new OpenFileDialog
        {
            InitialDirectory = Directory.Exists(SpikeEnv.MediaDir) ? SpikeEnv.MediaDir : null,
            Filter = "Видео|*.mp4;*.mkv;*.mov;*.avi;*.ts|Все файлы|*.*"
        };
        if (dlg.ShowDialog() != true) return;

        var res = p.Open(dlg.FileName);
        Log(res.Success
            ? $"открыт {Path.GetFileName(dlg.FileName)}: {p.Video.Codec} {p.Video.Width}x{p.Video.Height} {p.Video.FPS:F3} fps, HW={p.Video.VideoAcceleration}"
            : $"не открылся {dlg.FileName}: {res.Error}");
        UpdatePos();
    }

    private void StepNext_Click(object sender, RoutedEventArgs e) => Step(p => p.ShowFrameNext(), "вперёд");
    private void StepPrev_Click(object sender, RoutedEventArgs e) => Step(p => p.ShowFramePrev(), "назад");

    private void Step(Action<Player> step, string what)
    {
        var msA = PlayerA.Video.IsOpened ? StepTimer.Operation(PlayerA, () => step(PlayerA)) : double.NaN;
        var msB = PlayerB.Video.IsOpened ? StepTimer.Operation(PlayerB, () => step(PlayerB)) : double.NaN;
        Log($"шаг {what}: A {Fmt(msA)}, B {Fmt(msB)}");
        UpdatePos();

        static string Fmt(double ms) => double.IsNaN(ms) ? "—" : $"{ms:F1} мс";
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        PlayerA.TogglePlayPause();
        PlayerB.TogglePlayPause();
        UpdatePos();
    }

    private async void Bench_Click(object sender, RoutedEventArgs e) => await RunBenchAsync();

    private async Task RunBenchAsync()
    {
        BtnBench.IsEnabled = false;
        try
        {
            var files = Directory.Exists(SpikeEnv.MediaDir)
                ? Directory.GetFiles(SpikeEnv.MediaDir, "*.mp4").OrderBy(f => f).ToArray()
                : [];

            if (files.Length == 0)
            {
                Log($"нет тестовых роликов в {SpikeEnv.MediaDir}");
                return;
            }

            var runner = new BenchRunner(PlayerA, PlayerB, Log);
            var report = await Task.Run(() => runner.Run(files));
            Log($"отчёт: {report}");
        }
        catch (Exception ex)
        {
            Log("ОШИБКА: " + ex);
        }
        finally
        {
            BtnBench.IsEnabled = true;
        }
    }

    private void UpdatePos()
    {
        var fps = PlayerA.Video.FPS;
        var frame = fps > 0 ? (int)Math.Round(PlayerA.CurTime / 10_000_000.0 * fps) : 0;
        TxtPos.Text = $"A: {TimeSpan.FromTicks(PlayerA.CurTime):hh\\:mm\\:ss\\.fff} (кадр {frame})   B: {TimeSpan.FromTicks(PlayerB.CurTime):hh\\:mm\\:ss\\.fff}";
    }

    private void Log(string msg)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => Log(msg)); return; }
        TxtLog.AppendText($"{DateTime.Now:HH:mm:ss.fff}  {msg}{Environment.NewLine}");
        TxtLog.ScrollToEnd();
    }
}
