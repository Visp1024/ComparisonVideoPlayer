using System.Diagnostics;
using Spike.Mpv.Interop;

namespace Spike.Mpv.Bench;

/// <summary>Замер длительности одной транспортной операции libmpv и сводная статистика по серии.</summary>
public static class StepTimer
{
    /// <summary>Сколько ждать второй сигнал после первого (playback-restart и смену time-pos).</summary>
    private const int GraceMs = 150;

    /// <summary>
    /// Выполняет команду плеера и ждёт, пока libmpv сообщит о новом кадре. Сигналов два:
    /// смена свойства time-pos и событие playback-restart. Какой из них приходит на конкретную
    /// операцию — зависит от команды, поэтому засекаются оба, а стоимостью операции считается
    /// поздний из них (консервативная оценка «кадр действительно показан»).
    ///
    /// <paramref name="expectedPos"/> — ожидаемая позиция после операции, в секундах. Задавать
    /// её нужно всегда, когда она известна: на seek libmpv выставляет time-pos оптимистично,
    /// ещё до декода, поэтому одной смены свойства мало — замер закрывается только когда
    /// позиция совпала с ожидаемой И пришёл playback-restart (или истёк льготный интервал).
    ///
    /// Перед замером плеер «успокаивается» (Quiesce): хвосты событий от предыдущей операции
    /// иначе засчитываются этому замеру.
    ///
    /// Команда выполняется на отдельном потоке: mpv_command синхронна, и если ядро плеера
    /// встанет, без сторожевого потока повиснет весь прогон (в спайке FlyleafLib такое было).
    /// </summary>
    public static OpResult Operation(MpvPlayer p, Action action, double? expectedPos = null, double posToleranceSec = 0.0,
        bool requireRestart = false, int timeoutMs = 30_000)
    {
        p.Quiesce();

        var posGen0 = p.PositionGeneration;
        var restarts0 = p.PlaybackRestarts;
        var returned = false;
        Exception? failure = null;

        var t0 = Stopwatch.GetTimestamp();
        var worker = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
            finally { Volatile.Write(ref returned, true); }
        })
        { IsBackground = true, Name = "spike-mpv-op" };
        worker.Start();

        double msPos = double.NaN, msRestart = double.NaN, firstAt = double.NaN;

        while (true)
        {
            if (failure != null) throw new InvalidOperationException("команда плеера упала", failure);

            var elapsed = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;

            if (double.IsNaN(msPos) && p.PositionGeneration != posGen0 && PosReached(p)) msPos = elapsed;
            if (double.IsNaN(msRestart) && p.PlaybackRestarts != restarts0) msRestart = elapsed;

            // если ожидаемая позиция известна, замер закрывает только она: playback-restart
            // сам по себе может прийти и от «хвоста» предыдущей операции
            var got = expectedPos.HasValue
                ? !double.IsNaN(msPos)
                : !double.IsNaN(msPos) || !double.IsNaN(msRestart);

            // на seek и шаге назад решает playback-restart: time-pos libmpv выставляет
            // ещё до декода, и без этого условия замер закрывается за единицы миллисекунд,
            // а снимок берётся от старого кадра
            if (requireRestart && double.IsNaN(msRestart)) got = false;

            if (got && double.IsNaN(firstAt)) firstAt = elapsed;

            // оба сигнала пришли — операция точно завершена
            if (!double.IsNaN(msPos) && !double.IsNaN(msRestart) && Volatile.Read(ref returned))
                return new OpResult(msPos, msRestart, Math.Max(msPos, msRestart), false);

            // пришёл один — ждём второй недолго, потом закрываем замер
            if (got && Volatile.Read(ref returned) && elapsed - firstAt > GraceMs)
                return new OpResult(msPos, msRestart, Nz(msPos, msRestart), false);

            if (elapsed > timeoutMs)
            {
                if (!Volatile.Read(ref returned))
                    throw new PlayerHangException($"команда не вернулась за {elapsed:F0} мс");
                p.Quiesce();   // операция могла ещё идти — не тащим её хвост в следующий замер
                return new OpResult(msPos, msRestart, elapsed, true);   // кадра так и не появилось
            }

            Thread.Sleep(0);
        }

        static double Nz(double a, double b) => double.IsNaN(a) ? b : a;

        // сверяемся с pts показанного кадра, если сборка libmpv его отдаёт: time-pos на seek
        // выставляется оптимистично и «подтверждает» позицию раньше, чем кадр появился
        bool PosReached(MpvPlayer player)
        {
            if (!expectedPos.HasValue) return true;
            var shown = player.DisplayedFramePts;
            var actual = double.IsNaN(shown) ? player.Position : shown;
            return Math.Abs(actual - expectedPos.Value) <= posToleranceSec;
        }
    }

    public static Stats Summarize(IReadOnlyList<OpResult> samples)
    {
        var ok = samples.Where(s => !s.TimedOut).Select(s => s.Ms).OrderBy(s => s).ToArray();
        if (ok.Length == 0) return new Stats(0, 0, 0, 0, 0, samples.Count);

        return new Stats(
            Min: ok[0],
            Median: Percentile(ok, 0.50),
            Avg: ok.Average(),
            P95: Percentile(ok, 0.95),
            Max: ok[^1],
            Timeouts: samples.Count(s => s.TimedOut));
    }

    private static double Percentile(double[] sorted, double q)
    {
        var idx = (int)Math.Ceiling(q * sorted.Length) - 1;
        return sorted[Math.Clamp(idx, 0, sorted.Length - 1)];
    }
}

/// <summary>Результат одного замера: время до смены позиции, до playback-restart и итоговое.</summary>
public readonly record struct OpResult(double MsPos, double MsRestart, double Ms, bool TimedOut);

/// <summary>Команда libmpv не вернула управление — состояние плеера дальше считаем непригодным.</summary>
public sealed class PlayerHangException(string message) : Exception(message);

public readonly record struct Stats(double Min, double Median, double Avg, double P95, double Max, int Timeouts)
{
    public string Row => $"{Min:F1} | {Median:F1} | {Avg:F1} | {P95:F1} | {Max:F1}" + (Timeouts > 0 ? $" | таймаутов: {Timeouts}" : "");
}
