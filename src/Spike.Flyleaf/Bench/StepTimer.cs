using System.Diagnostics;
using FlyleafLib.MediaPlayer;

namespace Spike.Flyleaf.Bench;

/// <summary>Замер длительности одной транспортной операции и сводная статистика по серии.</summary>
public static class StepTimer
{
    /// <summary>
    /// Выполняет действие и ждёт, пока плеер реально покажет новый кадр
    /// (ShowFrame*/Seek у FlyleafLib могут отрабатывать асинхронно на потоке декодера).
    /// Возвращает время в миллисекундах; при таймауте — отрицательное значение.
    ///
    /// Вызов идёт на отдельном потоке: часть методов FlyleafLib умеет зависать намертво
    /// (см. отчёт спайка), и без сторожевого потока такой вызов вешает весь прогон.
    /// Плата — ~0,1 мс на создание потока, что на фоне единиц/десятков миллисекунд шага несущественно.
    /// </summary>
    public static double Operation(Player p, Action action, int timeoutMs = 10_000)
    {
        var framesBefore = p.Video.FramesDisplayed;
        var timeBefore = p.CurTime;
        var returned = false;
        Exception? failure = null;

        var t0 = Stopwatch.GetTimestamp();
        var worker = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
            finally { Volatile.Write(ref returned, true); }
        })
        { IsBackground = true, Name = "spike-op" };
        worker.Start();

        while (!Volatile.Read(ref returned) || (p.Video.FramesDisplayed == framesBefore && p.CurTime == timeBefore))
        {
            if (failure != null) throw new InvalidOperationException("операция плеера упала", failure);

            var elapsed = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            if (elapsed > timeoutMs)
            {
                if (!Volatile.Read(ref returned))
                    throw new PlayerHangException($"вызов не вернулся за {elapsed:F0} мс");
                return -elapsed;   // вызов вернулся, но нового кадра так и не появилось
            }

            Thread.Yield();
        }

        if (failure != null) throw new InvalidOperationException("операция плеера упала", failure);
        return Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
    }

    public static Stats Summarize(IReadOnlyList<double> samples)
    {
        var ok = samples.Where(s => s >= 0).OrderBy(s => s).ToArray();
        if (ok.Length == 0)
            return new Stats(0, 0, 0, 0, 0, samples.Count);

        return new Stats(
            Min: ok[0],
            Median: Percentile(ok, 0.50),
            Avg: ok.Average(),
            P95: Percentile(ok, 0.95),
            Max: ok[^1],
            Timeouts: samples.Count(s => s < 0));
    }

    private static double Percentile(double[] sorted, double q)
    {
        var idx = (int)Math.Ceiling(q * sorted.Length) - 1;
        return sorted[Math.Clamp(idx, 0, sorted.Length - 1)];
    }
}

/// <summary>Вызов FlyleafLib не вернул управление — состояние плеера дальше считаем непригодным.</summary>
public sealed class PlayerHangException(string message) : Exception(message);

public readonly record struct Stats(double Min, double Median, double Avg, double P95, double Max, int Timeouts)
{
    public string Row => $"{Min:F1} | {Median:F1} | {Avg:F1} | {P95:F1} | {Max:F1}" + (Timeouts > 0 ? $" | таймаутов: {Timeouts}" : "");
}
