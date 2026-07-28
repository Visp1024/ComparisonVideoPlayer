using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using FlyleafLib.MediaPlayer;

namespace Spike.Flyleaf.Bench;

/// <summary>
/// Автоматический прогон фазы 0: покадровый шаг вперёд/назад, шаг через границу GOP,
/// точность seek и работа двух плееров одновременно. Пишет markdown + json в OutDir.
/// Работает на фоновом потоке; плееры создаются заранее и привязаны к FlyleafHost.
/// </summary>
public sealed class BenchRunner(Player playerA, Player playerB, Action<string> log)
{
    private const int StepSamples = 30;
    private const int DualSamples = 20;

    private readonly List<object> _json = [];
    private readonly StringBuilder _md = new();
    private readonly List<string> _hangs = [];
    private bool _playersDead;

    /// <summary>Пошаговый лог с немедленным сбросом на диск — чтобы после зависания было видно, на каком вызове.</summary>
    private void Trace(string msg)
    {
        try { File.AppendAllText(Path.Combine(SpikeEnv.OutDir, "progress.log"), $"{DateTime.Now:HH:mm:ss.fff}  {msg}{Environment.NewLine}"); }
        catch { /* лог не критичен */ }
        log(msg);
    }

    public string Run(IReadOnlyList<string> files)
    {
        Directory.CreateDirectory(SpikeEnv.OutDir);

        _md.AppendLine("# Замеры спайка FlyleafLib");
        _md.AppendLine();
        _md.AppendLine($"- машина: {Environment.MachineName}, {Environment.ProcessorCount} логических ядер, .NET {Environment.Version}");
        _md.AppendLine($"- FlyleafLib 3.10.4, FFmpeg из `{SpikeEnv.FFmpegDir}`");
        _md.AppendLine($"- время прогона: {DateTime.Now:yyyy-MM-dd HH:mm}");
        _md.AppendLine();

        foreach (var f in files)
        {
            if (_playersDead) { _md.AppendLine($"`{Path.GetFileName(f)}` — пропущен: плеер завис на предыдущем тесте."); continue; }
            try { RunSingle(f); }
            catch (PlayerHangException ex) { RegisterHang($"{Path.GetFileName(f)}: {ex.Message}"); }
            catch (Exception ex) { Trace($"ОШИБКА на {Path.GetFileName(f)}: {ex}"); _md.AppendLine($"**ОШИБКА** на `{Path.GetFileName(f)}`: {ex.Message}"); }
        }

        if (files.Count >= 2 && !_playersDead)
        {
            try { RunDual(files[0], files[^1]); }
            catch (PlayerHangException ex) { RegisterHang($"парный тест: {ex.Message}"); }
            catch (Exception ex) { Trace($"ОШИБКА в парном тесте: {ex}"); _md.AppendLine($"**ОШИБКА** в парном тесте: {ex.Message}"); }
        }

        if (_hangs.Count > 0)
        {
            _md.AppendLine("## Зависшие вызовы FlyleafLib");
            _md.AppendLine();
            foreach (var h in _hangs) _md.AppendLine($"- {h}");
            _md.AppendLine();
        }

        var mdPath = Path.Combine(SpikeEnv.OutDir, "bench-results.md");
        File.WriteAllText(mdPath, _md.ToString(), Encoding.UTF8);
        File.WriteAllText(Path.Combine(SpikeEnv.OutDir, "bench-results.json"),
            JsonSerializer.Serialize(_json, new JsonSerializerOptions
            {
                WriteIndented = true,
                // в замерах есть NaN (нет эталона для сравнения) — иначе сериализация падает
                NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
            }));

        Trace($"готово, отчёт: {mdPath}");
        return mdPath;
    }

    private void RunSingle(string file)
    {
        var name = Path.GetFileName(file);
        Trace($"=== {name} ===");

        var probe = MediaProbe.Describe(file);
        var openMs = Time(() => Open(playerA, file));
        var v = playerA.Video;
        var fps = v.FPS;
        Trace($"открыт за {openMs:F0} мс: {probe}, HW-декод: {v.VideoAcceleration}");

        _md.AppendLine($"## {name}");
        _md.AppendLine();
        _md.AppendLine($"`{probe}, аппаратный декод: {(v.VideoAcceleration ? "да" : "нет")}, открытие {openMs:F0} мс`");
        _md.AppendLine();

        // рабочая точка — середина файла, вне первого GOP
        Trace("переход в середину файла + прогрев");
        SeekToFrame(playerA, (int)(v.FramesTotal / 2), fps);
        for (int i = 0; i < 5; i++) StepTimer.Operation(playerA, playerA.ShowFrameNext); // прогрев

        Trace("серия шагов вперёд");
        var fwd = new List<double>();
        for (int i = 0; i < StepSamples; i++) fwd.Add(StepTimer.Operation(playerA, playerA.ShowFrameNext));

        Trace("серия шагов назад");
        var back = new List<double>();
        for (int i = 0; i < StepSamples; i++) back.Add(StepTimer.Operation(playerA, playerA.ShowFramePrev));

        var sFwd = StepTimer.Summarize(fwd);
        var sBack = StepTimer.Summarize(back);

        // худший случай: шаг назад через границу GOP (текущий кадр — keyframe, предыдущий лежит в прошлом GOP)
        var boundary = new List<double>();
        foreach (var kf in KeyframeCandidates(v.FramesTotal))
        {
            Trace($"шаг назад через границу GOP: кадр {kf} -> {kf - 1}");
            SeekToFrame(playerA, kf, fps);
            boundary.Add(StepTimer.Operation(playerA, playerA.ShowFramePrev));
        }
        var sBoundary = StepTimer.Summarize(boundary);

        _md.AppendLine("| операция | min | медиана | среднее | p95 | max |");
        _md.AppendLine("|---|---|---|---|---|---|");
        _md.AppendLine($"| шаг вперёд (x{StepSamples}) | {sFwd.Row} |");
        _md.AppendLine($"| шаг назад (x{StepSamples}) | {sBack.Row} |");
        _md.AppendLine($"| шаг назад через границу GOP (x{boundary.Count}) | {sBoundary.Row} |");
        _md.AppendLine();

        Trace($"шаг вперёд: медиана {sFwd.Median:F1} мс; назад: медиана {sBack.Median:F1} мс; назад через GOP: медиана {sBoundary.Median:F1} мс");

        var seek = SeekAccuracy(file, fps, (int)v.FramesTotal);

        _json.Add(new
        {
            file = name,
            codec = v.Codec,
            v.Width,
            v.Height,
            fps,
            frames = v.FramesTotal,
            hwAccel = v.VideoAcceleration,
            openMs,
            stepForward = sFwd,
            stepBackward = sBack,
            stepBackwardAcrossGop = sBoundary,
            seek
        });
    }

    private void RegisterHang(string what)
    {
        _playersDead = true;
        _hangs.Add(what);
        Trace($"ЗАВИСАНИЕ: {what} — дальнейшие тесты пропускаются, состояние плеера непригодно");
    }

    /// <summary>Кандидаты в границы GOP: кратные 250 и 25 — так закодированы тестовые ролики.</summary>
    private static IEnumerable<int> KeyframeCandidates(long totalFrames)
    {
        foreach (var f in new[] { 250, 500, 750 })
            if (f < totalFrames - 2) yield return f;
    }

    private List<object> SeekAccuracy(string file, double fps, int totalFrames)
    {
        var targets = new[] { 0, 1, 249, 250, 251, 449, 700, Math.Max(0, totalFrames - 2) }
            .Where(t => t < totalFrames).Distinct().ToArray();

        _md.AppendLine("| целевой кадр | способ | время, мс | CurTime, мс | кадр по CurTime | Δ кадров | MAD: свой кадр / соседний |");
        _md.AppendLine("|---|---|---|---|---|---|---|");

        var rows = new List<object>();

        // Проход 1: ShowFrame(idx) — прямой переход к кадру по индексу, с проверкой картинки
        foreach (var target in targets)
        {
            Trace($"seek: ShowFrame({target})");
            var ms = StepTimer.Operation(playerA, () => playerA.ShowFrame(target));
            var curMs = playerA.CurTime / 10_000.0;
            var actual = (int)Math.Round(curMs / 1000.0 * fps);

            double mad = double.NaN, madControl = double.NaN;
            var baseName = Path.GetFileNameWithoutExtension(file);
            var shot = Path.Combine(SpikeEnv.OutDir, $"{baseName}_f{target}_player.png");
            var refPng = Path.Combine(SpikeEnv.OutDir, $"{baseName}_f{target}_ffmpeg.png");
            var refNext = Path.Combine(SpikeEnv.OutDir, $"{baseName}_f{target + 1}_ffmpeg_control.png");
            try
            {
                playerA.TakeSnapshotToFile(shot, 640, 360);
                if (FrameCompare.TryExtractReference(file, target, refPng, out _))
                    mad = FrameCompare.MeanAbsDiff(shot, refPng);

                // контроль: та же картинка против СОСЕДНЕГО кадра. Если разница сравнима с mad —
                // метрика ничего не доказывает; если заметно больше — кадр действительно тот самый.
                if (target + 1 < totalFrames && FrameCompare.TryExtractReference(file, target + 1, refNext, out _))
                    madControl = FrameCompare.MeanAbsDiff(shot, refNext);
            }
            catch (Exception ex) { Trace($"снимок кадра {target}: {ex.Message}"); }

            _md.AppendLine($"| {target} | ShowFrame | {ms:F1} | {curMs:F1} | {actual} | {actual - target} | {Fmt(mad)} / {Fmt(madControl)} |");
            rows.Add(new { target, method = "ShowFrame", ms, curMs, actual, delta = actual - target, mad, madControl });

            static string Fmt(double d) => double.IsNaN(d) ? "—" : d.ToString("F2");
        }

        // Проход 2: SeekAccurate(ms) — переход по времени, как будет делать таймлайн.
        // Отдельным проходом: смешение двух способов подряд роняло плеер в вечное ожидание.
        foreach (var target in targets)
        {
            var targetMs = (int)Math.Round(target * 1000.0 / fps);
            Trace($"seek: SeekAccurate({targetMs} мс, кадр {target})");
            var ms2 = MeasureSeek(playerA, targetMs);
            var curMs2 = playerA.CurTime / 10_000.0;
            var actual2 = (int)Math.Round(curMs2 / 1000.0 * fps);
            _md.AppendLine($"| {target} | SeekAccurate | {ms2:F1} | {curMs2:F1} | {actual2} | {actual2 - target} | — |");
            rows.Add(new { target, method = "SeekAccurate", ms = ms2, curMs = curMs2, actual = actual2, delta = actual2 - target, mad = double.NaN });
        }

        _md.AppendLine();
        return rows;
    }

    private void RunDual(string fileA, string fileB)
    {
        Trace("=== два плеера одновременно ===");
        _md.AppendLine("## Два плеера одновременно");
        _md.AppendLine();
        _md.AppendLine($"A: `{Path.GetFileName(fileA)}`, B: `{Path.GetFileName(fileB)}`");
        _md.AppendLine();

        Open(playerA, fileA);
        Open(playerB, fileB);

        var fpsA = playerA.Video.FPS;
        var fpsB = playerB.Video.FPS;
        SeekToFrame(playerA, (int)(playerA.Video.FramesTotal / 2), fpsA);
        SeekToFrame(playerB, (int)(playerB.Video.FramesTotal / 2), fpsB);

        var proc = Process.GetCurrentProcess();
        var cpu0 = proc.TotalProcessorTime;
        var sw = Stopwatch.StartNew();

        var fwdA = new List<double>();
        var fwdB = new List<double>();
        for (int i = 0; i < DualSamples; i++)
        {
            fwdA.Add(StepTimer.Operation(playerA, playerA.ShowFrameNext));
            fwdB.Add(StepTimer.Operation(playerB, playerB.ShowFrameNext));
        }

        var backA = new List<double>();
        var backB = new List<double>();
        for (int i = 0; i < DualSamples; i++)
        {
            backA.Add(StepTimer.Operation(playerA, playerA.ShowFramePrev));
            backB.Add(StepTimer.Operation(playerB, playerB.ShowFramePrev));
        }

        sw.Stop();
        proc.Refresh();
        var cpuMs = (proc.TotalProcessorTime - cpu0).TotalMilliseconds;
        var ramMb = proc.WorkingSet64 / 1024.0 / 1024.0;

        // синхронное воспроизведение обоих потоков: сколько реально проиграно и расхождение позиций
        Trace("5 секунд одновременного воспроизведения");
        var posA0 = playerA.CurTime;
        var posB0 = playerB.CurTime;
        playerA.Play();
        playerB.Play();
        Thread.Sleep(5000);
        var playedA = (playerA.CurTime - posA0) / 10_000.0;
        var playedB = (playerB.CurTime - posB0) / 10_000.0;
        var driftMs = Math.Abs(playedA - playedB);
        playerA.Pause();
        playerB.Pause();
        var droppedA = playerA.Video.FramesDropped;
        var droppedB = playerB.Video.FramesDropped;
        var fpsCurA = playerA.Video.FPSCurrent;
        var fpsCurB = playerB.Video.FPSCurrent;

        var sfa = StepTimer.Summarize(fwdA);
        var sfb = StepTimer.Summarize(fwdB);
        var sba = StepTimer.Summarize(backA);
        var sbb = StepTimer.Summarize(backB);

        _md.AppendLine("| операция | min | медиана | среднее | p95 | max |");
        _md.AppendLine("|---|---|---|---|---|---|");
        _md.AppendLine($"| A: шаг вперёд (x{DualSamples}) | {sfa.Row} |");
        _md.AppendLine($"| B: шаг вперёд (x{DualSamples}) | {sfb.Row} |");
        _md.AppendLine($"| A: шаг назад (x{DualSamples}) | {sba.Row} |");
        _md.AppendLine($"| B: шаг назад (x{DualSamples}) | {sbb.Row} |");
        _md.AppendLine();
        _md.AppendLine($"- серия шагов заняла {sw.Elapsed.TotalSeconds:F1} с, CPU-время процесса {cpuMs:F0} мс (~{cpuMs / sw.Elapsed.TotalMilliseconds * 100:F0}% одного ядра суммарно), RAM (working set) {ramMb:F0} МБ");
        _md.AppendLine($"- 5 секунд одновременного воспроизведения: проиграно A/B {playedA:F0}/{playedB:F0} мс (расхождение {driftMs:F0} мс), FPS A/B {fpsCurA:F1}/{fpsCurB:F1}, потеряно кадров A/B {droppedA}/{droppedB}");
        _md.AppendLine();

        Trace($"парный режим: A медиана вперёд {sfa.Median:F1} мс / назад {sba.Median:F1} мс; B {sfb.Median:F1} / {sbb.Median:F1}; RAM {ramMb:F0} МБ; дрейф {driftMs:F0} мс");

        _json.Add(new
        {
            test = "dual",
            fileA = Path.GetFileName(fileA),
            fileB = Path.GetFileName(fileB),
            forwardA = sfa,
            forwardB = sfb,
            backwardA = sba,
            backwardB = sbb,
            cpuMs,
            wallMs = sw.Elapsed.TotalMilliseconds,
            ramMb,
            playedA,
            playedB,
            driftMs,
            fpsCurA,
            fpsCurB,
            droppedA,
            droppedB
        });
    }

    /// <summary>
    /// Открытие файла. Player.Open синхронный и возвращает управление с уже открытым потоком;
    /// ждать показанного кадра по Video.FramesDisplayed/Width нельзя — на паузе эта статистика
    /// не обновляется (проверено: висит нулевой до старта воспроизведения).
    /// </summary>
    private void Open(Player p, string file)
    {
        var res = p.Open(file);
        if (!res.Success) throw new InvalidOperationException($"не открылся {file}: {res.Error}");

        var sw = Stopwatch.StartNew();
        while (!p.Video.IsOpened && sw.ElapsedMilliseconds < 5_000) Thread.Sleep(5);
    }

    /// <summary>
    /// Время точного seek. CurTime после SeekAccurate меняется сразу (позиция выставляется
    /// оптимистично, до декода), поэтому ждём события SeekCompleted — оно приходит,
    /// когда кадр действительно готов.
    /// </summary>
    private static double MeasureSeek(Player p, int targetMs, int timeoutMs = 10_000)
    {
        using var done = new ManualResetEventSlim(false);
        void OnSeek(object? s, int ms) => done.Set();

        p.SeekCompleted += OnSeek;
        try
        {
            var t0 = Stopwatch.GetTimestamp();
            p.SeekAccurate(targetMs);
            var fired = done.Wait(timeoutMs);
            var elapsed = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            return fired ? elapsed : -elapsed;
        }
        finally { p.SeekCompleted -= OnSeek; }
    }

    private void SeekToFrame(Player p, int frame, double fps)
    {
        StepTimer.Operation(p, () => p.ShowFrame(frame));
        Thread.Sleep(100);
    }

    private static double Time(Action a)
    {
        var t0 = Stopwatch.GetTimestamp();
        a();
        return Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
    }
}
