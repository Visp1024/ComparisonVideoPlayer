using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Spike.Mpv.Interop;

namespace Spike.Mpv.Bench;

/// <summary>
/// Автоматический прогон фазы 0б: те же сценарии, что и в спайке FlyleafLib
/// (шаг вперёд/назад, шаг через границу GOP, точность seek, два плеера одновременно) —
/// на тех же роликах, чтобы цифры были сравнимы. Пишет markdown + json в OutDir.
/// Выполняется на фоновом потоке, плееры уже привязаны к HWND.
/// </summary>
public sealed class BenchRunner(MpvPlayer playerA, MpvPlayer playerB, Action<string> log)
{
    private const int StepSamples = 30;
    private const int DualSamples = 20;

    private readonly List<object> _json = [];
    private readonly StringBuilder _md = new();
    private readonly List<string> _hangs = [];
    private bool _playersDead;

    /// <summary>Пошаговый лог с немедленным сбросом на диск — чтобы после зависания было видно, где встали.</summary>
    private void Trace(string msg)
    {
        try { File.AppendAllText(Path.Combine(SpikeEnv.OutDir, "progress.log"), $"{DateTime.Now:HH:mm:ss.fff}  {msg}{Environment.NewLine}"); }
        catch { /* лог не критичен */ }
        log(msg);
    }

    public string Run(IReadOnlyList<string> files)
    {
        Directory.CreateDirectory(SpikeEnv.OutDir);

        _md.AppendLine("# Замеры спайка libmpv");
        _md.AppendLine();
        _md.AppendLine($"- машина: {Environment.MachineName}, {Environment.ProcessorCount} логических ядер, .NET {Environment.Version}");
        _md.AppendLine($"- libmpv из `{SpikeEnv.MpvDir}`, client API {MpvPlayer.ApiVersion}, mpv {playerA.GetString("mpv-version")}");
        _md.AppendLine($"- время прогона: {DateTime.Now:yyyy-MM-dd HH:mm}");
        _md.AppendLine();

        if (files.Count > 0) DiagnoseEvents(files[0]);

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
            _md.AppendLine("## Зависшие вызовы libmpv");
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
                NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
            }));

        Trace($"готово, отчёт: {mdPath}");
        return mdPath;
    }

    /// <summary>
    /// Какие события libmpv шлёт на frame-step / frame-back-step / точный seek — от этого
    /// зависит, по какому сигналу вообще можно мерить «кадр показан». Пишется в отчёт как есть.
    /// </summary>
    private void DiagnoseEvents(string file)
    {
        Trace("=== диагностика: какие события приходят на каждую операцию ===");
        playerA.LoadFile(file);
        var fps = Fps(playerA);
        SeekToFrame(playerA, 400, fps);

        _md.AppendLine("## Сигналы завершения операции");
        _md.AppendLine();
        _md.AppendLine("| операция | события libmpv | время до смены time-pos, мс | до playback-restart, мс |");
        _md.AppendLine("|---|---|---|---|");

        foreach (var (name, act) in new (string, Action)[]
                 {
                     ("frame-step", () => playerA.Command("frame-step")),
                     ("frame-back-step", () => playerA.Command("frame-back-step")),
                     ("seek absolute+exact", () => SeekToTime(playerA, 12.0)),
                 })
        {
            playerA.BeginEventTrace();
            var r = StepTimer.Operation(playerA, act);
            var events = playerA.EndEventTrace();
            var uniq = string.Join(", ", events.Select(e => e.Split(' ')[0]).Distinct());
            _md.AppendLine($"| `{name}` | {uniq} | {Fmt(r.MsPos)} | {Fmt(r.MsRestart)} |");
            Trace($"{name}: события [{uniq}], time-pos {Fmt(r.MsPos)} мс, playback-restart {Fmt(r.MsRestart)} мс");
        }

        _md.AppendLine();
        _md.AppendLine("Стоимостью операции ниже считается момент, когда `video-frame-info/pts` показал ожидаемый кадр " +
                       $"(свойство доступно: {(playerA.HasDisplayedFramePts ? "да" : "нет, замер идёт по time-pos")}), " +
                       "но не раньше playback-restart.");
        _md.AppendLine();

        static string Fmt(double d) => double.IsNaN(d) ? "—" : d.ToString("F1");
    }

    private void RunSingle(string file)
    {
        var name = Path.GetFileName(file);
        Trace($"=== {name} ===");

        var openMs = playerA.LoadFile(file);
        var fps = Fps(playerA);
        var frames = (int)playerA.GetLong("estimated-frame-count", 0);
        var codec = playerA.GetString("video-codec");
        var hwdec = playerA.GetString("hwdec-current");
        var w = playerA.GetLong("width");
        var h = playerA.GetLong("height");
        Trace($"открыт за {openMs:F0} мс: {codec} {w}x{h} {fps:F3} fps, {frames} кадров, hwdec={hwdec}");

        _md.AppendLine($"## {name}");
        _md.AppendLine();
        _md.AppendLine($"`{codec} {w}x{h}, {fps:F3} fps, {frames} кадров, hwdec: {hwdec}, открытие {openMs:F0} мс`");
        _md.AppendLine();

        // рабочая точка — середина файла, вне первого GOP
        Trace("переход в середину файла + прогрев");
        SeekToFrame(playerA, frames / 2, fps);
        for (int i = 0; i < 5; i++) StepForward(playerA, fps);

        Trace("серия шагов вперёд");
        var fwd = new List<OpResult>();
        for (int i = 0; i < StepSamples; i++) fwd.Add(StepForward(playerA, fps));

        // Шаг вперёд у mpv упирается в показ кадра «по расписанию» (длительность кадра),
        // а не в декод. untimed=yes снимает расписание — разница показывает, где предел.
        Trace("серия шагов вперёд с untimed=yes");
        playerA.SetProperty("untimed", "yes");
        var fwdUntimed = new List<OpResult>();
        for (int i = 0; i < StepSamples; i++) fwdUntimed.Add(StepForward(playerA, fps));
        playerA.SetProperty("untimed", "no");

        Trace("серия шагов назад");
        var back = new List<OpResult>();
        for (int i = 0; i < StepSamples; i++) back.Add(StepBack(playerA, fps));

        var sFwd = StepTimer.Summarize(fwd);
        var sFwdUntimed = StepTimer.Summarize(fwdUntimed);
        var sBack = StepTimer.Summarize(back);

        // худший случай: шаг назад через границу GOP (текущий кадр — keyframe, предыдущий в прошлом GOP)
        var boundary = new List<OpResult>();
        foreach (var kf in new[] { 250, 500, 750 }.Where(k => k < frames - 2))
        {
            Trace($"шаг назад через границу GOP: кадр {kf} -> {kf - 1}");
            SeekToFrame(playerA, kf, fps);
            boundary.Add(StepBack(playerA, fps));
        }
        var sBoundary = StepTimer.Summarize(boundary);

        _md.AppendLine("| операция | min | медиана | среднее | p95 | max |");
        _md.AppendLine("|---|---|---|---|---|---|");
        _md.AppendLine($"| шаг вперёд (x{StepSamples}) | {sFwd.Row} |");
        _md.AppendLine($"| шаг вперёд, untimed=yes (x{StepSamples}) | {sFwdUntimed.Row} |");
        _md.AppendLine($"| шаг назад (x{StepSamples}) | {sBack.Row} |");
        _md.AppendLine($"| шаг назад через границу GOP (x{boundary.Count}) | {sBoundary.Row} |");
        _md.AppendLine();

        Trace($"шаг вперёд: медиана {sFwd.Median:F1} мс (untimed {sFwdUntimed.Median:F1} мс); назад: медиана {sBack.Median:F1} мс; назад через GOP: медиана {sBoundary.Median:F1} мс");

        var seek = SeekAccuracy(file, fps, frames);

        _json.Add(new
        {
            file = name,
            codec,
            width = w,
            height = h,
            fps,
            frames,
            hwdec,
            openMs,
            stepForward = sFwd,
            stepForwardUntimed = sFwdUntimed,
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

    /// <summary>
    /// Точность seek: переход к кадру N по времени (единственный способ у mpv — секунды),
    /// затем снимок кадра и распознавание номера по впечатанной в картинку цифре.
    /// </summary>
    private List<object> SeekAccuracy(string file, double fps, int totalFrames)
    {
        var targets = new[] { 0, 1, 249, 250, 251, 449, 700, Math.Max(0, totalFrames - 2) }
            .Where(t => t < totalFrames).Distinct().OrderBy(t => t).ToArray();

        _md.AppendLine("| целевой кадр | время seek, мс | time-pos, мс | pts показанного кадра, мс | кадр по pts | кадр по картинке | Δ кадров (картинка) |");
        _md.AppendLine("|---|---|---|---|---|---|---|");

        var rows = new List<object>();
        var baseName = Path.GetFileNameWithoutExtension(file);

        foreach (var target in targets)
        {
            Trace($"seek к кадру {target}");
            var r = SeekToFrame(playerA, target, fps);
            var posMs = playerA.Position * 1000.0;
            var ptsSec = playerA.DisplayedFramePts;
            var byPts = double.IsNaN(ptsSec) ? -1 : (int)Math.Round(ptsSec * fps);

            var recognized = RecognizeFrame(file, baseName, target, fps, totalFrames);

            _md.AppendLine($"| {target} | {r.Ms:F1} | {posMs:F1} | {(double.IsNaN(ptsSec) ? "—" : (ptsSec * 1000).ToString("F1"))} | {(byPts < 0 ? "—" : byPts.ToString())} | " +
                           $"{(recognized < 0 ? "—" : recognized.ToString())} | {(recognized < 0 ? "—" : (recognized - target).ToString())} |");
            rows.Add(new { target, ms = r.Ms, posMs, ptsMs = ptsSec * 1000, byPts, recognized, delta = recognized < 0 ? (int?)null : recognized - target });
        }

        _md.AppendLine();
        _md.AppendLine("«Кадр по картинке» — снимок плеера сопоставлен с эталонами ffmpeg для кадров N-1/N/N+1 " +
                       "по области с впечатанным номером; берётся ближайший.");
        _md.AppendLine();
        return rows;
    }

    /// <summary>Снимок текущего кадра и его распознавание среди эталонов N-1, N, N+1.</summary>
    private int RecognizeFrame(string file, string baseName, int target, double fps, int totalFrames)
    {
        try
        {
            var shot = Path.Combine(SpikeEnv.OutDir, $"{baseName}_f{target}_player.png");
            playerA.Quiesce();   // снимок должен быть уже нового кадра, а не предыдущего
            playerA.Command("screenshot-to-file", shot, "video");

            var best = -1;
            var bestDiff = double.MaxValue;
            foreach (var cand in new[] { target - 1, target, target + 1 }.Where(c => c >= 0 && c < totalFrames))
            {
                var refPng = Path.Combine(SpikeEnv.OutDir, $"{baseName}_ref_f{cand}.png");
                if (!FrameCompare.TryExtractReference(file, cand, fps, refPng)) continue;

                var diff = FrameCompare.NumberBoxDiff(shot, refPng);
                if (double.IsNaN(diff)) continue;
                if (diff < bestDiff) { bestDiff = diff; best = cand; }
            }
            return best;
        }
        catch (Exception ex)
        {
            Trace($"снимок/сравнение кадра {target}: {ex.Message}");
            return -1;
        }
    }

    private void RunDual(string fileA, string fileB)
    {
        Trace("=== два плеера одновременно ===");
        _md.AppendLine("## Два плеера одновременно");
        _md.AppendLine();
        _md.AppendLine($"A: `{Path.GetFileName(fileA)}`, B: `{Path.GetFileName(fileB)}`");
        _md.AppendLine();

        playerA.LoadFile(fileA);
        playerB.LoadFile(fileB);

        var fpsA = Fps(playerA);
        var fpsB = Fps(playerB);
        SeekToFrame(playerA, (int)playerA.GetLong("estimated-frame-count", 900) / 2, fpsA);
        SeekToFrame(playerB, (int)playerB.GetLong("estimated-frame-count", 900) / 2, fpsB);

        var proc = Process.GetCurrentProcess();
        var cpu0 = proc.TotalProcessorTime;
        var sw = Stopwatch.StartNew();

        var fwdA = new List<OpResult>();
        var fwdB = new List<OpResult>();
        for (int i = 0; i < DualSamples; i++)
        {
            fwdA.Add(StepForward(playerA, fpsA));
            fwdB.Add(StepForward(playerB, fpsB));
        }

        var backA = new List<OpResult>();
        var backB = new List<OpResult>();
        for (int i = 0; i < DualSamples; i++)
        {
            backA.Add(StepBack(playerA, fpsA));
            backB.Add(StepBack(playerB, fpsB));
        }

        sw.Stop();
        proc.Refresh();
        var cpuMs = (proc.TotalProcessorTime - cpu0).TotalMilliseconds;
        var ramMb = proc.WorkingSet64 / 1024.0 / 1024.0;

        Trace("5 секунд одновременного воспроизведения");
        var posA0 = playerA.Position;
        var posB0 = playerB.Position;
        playerA.SetProperty("pause", "no");
        playerB.SetProperty("pause", "no");
        Thread.Sleep(5000);
        var playedA = (playerA.Position - posA0) * 1000.0;
        var playedB = (playerB.Position - posB0) * 1000.0;
        playerA.SetProperty("pause", "yes");
        playerB.SetProperty("pause", "yes");
        var driftMs = Math.Abs(playedA - playedB);
        var fpsCurA = playerA.GetDouble("estimated-display-fps");
        var droppedA = playerA.GetLong("frame-drop-count", 0);
        var droppedB = playerB.GetLong("frame-drop-count", 0);
        var voDropA = playerA.GetLong("vo-delayed-frame-count", 0);
        var voDropB = playerB.GetLong("vo-delayed-frame-count", 0);

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
        _md.AppendLine($"- 5 секунд одновременного воспроизведения: проиграно A/B {playedA:F0}/{playedB:F0} мс (расхождение {driftMs:F0} мс), " +
                       $"частота дисплея {fpsCurA:F1}, потеряно кадров A/B {droppedA}/{droppedB}, задержанных кадров VO A/B {voDropA}/{voDropB}");
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
            droppedA,
            droppedB,
            voDropA,
            voDropB
        });
    }

    /// <summary>Шаг на один кадр вперёд с проверкой, что позиция действительно сдвинулась на кадр.</summary>
    private static OpResult StepForward(MpvPlayer p, double fps)
    {
        p.Quiesce();
        return StepTimer.Operation(p, () => p.Command("frame-step"), ShownPos(p) + 1.0 / fps, 0.4 / fps);
    }

    /// <summary>Шаг на один кадр назад с той же проверкой.</summary>
    private static OpResult StepBack(MpvPlayer p, double fps)
    {
        p.Quiesce();
        return StepTimer.Operation(p, () => p.Command("frame-back-step"), ShownPos(p) - 1.0 / fps, 0.4 / fps, requireRestart: true);
    }

    /// <summary>Позиция показанного кадра: pts кадра, если libmpv его отдаёт, иначе time-pos.</summary>
    private static double ShownPos(MpvPlayer p)
    {
        var pts = p.DisplayedFramePts;
        return double.IsNaN(pts) ? p.Position : pts;
    }

    /// <summary>fps контейнера; при отсутствии — оценка по видеофильтру.</summary>
    private static double Fps(MpvPlayer p)
    {
        var fps = p.GetDouble("container-fps");
        if (double.IsNaN(fps) || fps <= 0) fps = p.GetDouble("estimated-vf-fps");
        return double.IsNaN(fps) || fps <= 0 ? 30.0 : fps;
    }

    private static void SeekToTime(MpvPlayer p, double seconds)
        => p.Command("seek", seconds.ToString("F6", CultureInfo.InvariantCulture), "absolute+exact");

    /// <summary>Точный переход к кадру: seek в середину кадра, ожидание реально показанного кадра.</summary>
    private static OpResult SeekToFrame(MpvPlayer p, int frame, double fps)
    {
        var target = (frame + 0.5) / fps;
        // ждём, пока показан именно кадр N: его pts = N/fps. Допуск 0,6 кадра — чтобы условие
        // работало и на сборке без video-frame-info, где сверка идёт с time-pos (тот стоит
        // в запрошенной точке, на полкадра позже pts)
        return StepTimer.Operation(p, () => SeekToTime(p, target), frame / fps, 0.6 / fps, requireRestart: true);
    }
}
