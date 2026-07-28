using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using Spike.Mpv.Bench;
using Spike.Mpv.Interop;

namespace Spike.Mpv;

/// <summary>
/// Окно спайка: два независимых экземпляра libmpv рядом (каждый рендерит в своё дочернее окно),
/// ручной покадровый шаг с клавиатуры и кнопка автопрогона замеров (то же, что --bench).
/// </summary>
public partial class MainWindow : Window
{
    private MpvPlayer? _a;
    private MpvPlayer? _b;
    private double _fpsA = 30;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        KeyDown += OnKeyDown;
        Closed += (_, _) => { _a?.Dispose(); _b?.Dispose(); };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Log($"libmpv: {SpikeEnv.MpvDir}");
        Log($"FFmpeg (эталонные кадры): {SpikeEnv.FFmpegDir}");
        Log($"тестовые ролики: {SpikeEnv.MediaDir}");
        Log($"результаты: {SpikeEnv.OutDir}");

        // HWND дочерних окон существует только после загрузки контролов
        _a = new MpvPlayer("A", HostA.VideoHandle, Log);
        _b = new MpvPlayer("B", HostB.VideoHandle, Log);
        Log($"client API {MpvPlayer.ApiVersion}, mpv {_a.GetString("mpv-version")}");

        if (App.OpenPath is { } path)
        {
            foreach (var p in new[] { _a, _b })
            {
                p.LoadFile(path);
                _fpsA = p.GetDouble("container-fps");
                // разные кадры в двух плеерах — сразу видно, что окна независимы
                p.Command("seek", p == _a ? "10.0" : "20.0", "absolute+exact");
            }
            Log($"открыт в обоих плеерах: {Path.GetFileName(path)}");
            UpdatePos();
        }

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

    private void OpenA_Click(object sender, RoutedEventArgs e) => OpenInto(_a);
    private void OpenB_Click(object sender, RoutedEventArgs e) => OpenInto(_b);

    private void OpenInto(MpvPlayer? p)
    {
        if (p is null) return;

        var dlg = new OpenFileDialog
        {
            InitialDirectory = Directory.Exists(SpikeEnv.MediaDir) ? SpikeEnv.MediaDir : null,
            Filter = "Видео|*.mp4;*.mkv;*.mov;*.avi;*.ts|Все файлы|*.*"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var ms = p.LoadFile(dlg.FileName);
            Log($"[{p.Name}] открыт {Path.GetFileName(dlg.FileName)} за {ms:F0} мс: " +
                $"{p.GetString("video-codec")} {p.GetLong("width")}x{p.GetLong("height")} " +
                $"{p.GetDouble("container-fps"):F3} fps, hwdec={p.GetString("hwdec-current")}");
            if (p == _a) _fpsA = p.GetDouble("container-fps");
        }
        catch (Exception ex) { Log($"[{p.Name}] не открылся: {ex.Message}"); }

        UpdatePos();
    }

    private void StepNext_Click(object sender, RoutedEventArgs e) => Step("frame-step", "вперёд");
    private void StepPrev_Click(object sender, RoutedEventArgs e) => Step("frame-back-step", "назад");

    private void Step(string command, string what)
    {
        var msA = Measure(_a, command);
        var msB = Measure(_b, command);
        Log($"шаг {what}: A {Fmt(msA)}, B {Fmt(msB)}");
        UpdatePos();

        static double Measure(MpvPlayer? p, string cmd)
        {
            if (p is null || p.GetLong("width", 0) <= 0) return double.NaN;
            return StepTimer.Operation(p, () => p.Command(cmd)).Ms;
        }

        static string Fmt(double ms) => double.IsNaN(ms) ? "—" : $"{ms:F1} мс";
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        foreach (var p in new[] { _a, _b })
            p?.SetProperty("pause", p.GetString("pause") == "yes" ? "no" : "yes");
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

            var runner = new BenchRunner(_a!, _b!, Log);
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
        var posA = _a?.Position ?? double.NaN;
        var posB = _b?.Position ?? double.NaN;
        var frame = double.IsNaN(posA) || _fpsA <= 0 ? 0 : (int)Math.Round(posA * _fpsA - 0.5);
        TxtPos.Text = $"A: {Fmt(posA)} (кадр {frame})   B: {Fmt(posB)}";

        static string Fmt(double sec) => double.IsNaN(sec) ? "—" : TimeSpan.FromSeconds(sec).ToString(@"hh\:mm\:ss\.fff");
    }

    private void Log(string msg)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => Log(msg)); return; }
        TxtLog.AppendText($"{DateTime.Now:HH:mm:ss.fff}  {msg}{Environment.NewLine}");
        TxtLog.ScrollToEnd();
    }
}
