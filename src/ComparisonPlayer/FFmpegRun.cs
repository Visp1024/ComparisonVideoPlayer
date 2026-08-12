using System.Diagnostics;
using System.Globalization;
using System.Text;
using ComparisonPlayer.Localization;

namespace ComparisonPlayer;

/// <param name="Percent">Готовность, 0..100.</param>
/// <param name="Frame">Сколько кадров обработано.</param>
/// <param name="Total">Сколько кадров ожидается всего.</param>
/// <param name="Eta">Оценка оставшегося времени.</param>
/// <param name="Speed">Скорость относительно реального времени, как её сообщает ffmpeg.</param>
public readonly record struct FFmpegProgress(double Percent, long Frame, long Total, TimeSpan Eta, double Speed);

/// <summary>
/// Запуск ffmpeg CLI с разбором блоков <c>-progress</c>. Один на всё приложение:
/// так работают и сборка прокси кэша с миниатюрами (фаза 4), и вырезание отрезка
/// в отдельный файл (задача #40) — отличаются они только аргументами.
/// </summary>
public static class FFmpegRun
{
    /// <summary>
    /// Выполнить ffmpeg и дождаться конца. Блок <c>-progress</c> приходит построчно
    /// парами «ключ=значение» и заканчивается строкой <c>progress=</c> — по ней и
    /// сообщаем прогресс наружу.
    /// </summary>
    /// <param name="args">Аргументы командной строки, включая <c>-progress pipe:1</c>.</param>
    /// <param name="total">Сколько кадров ожидается: из них считается процент и оценка.</param>
    /// <param name="progress">
    /// Вызывается из потока чтения вывода, а не из потока вызывающего: подписчику,
    /// который трогает интерфейс, нужен собственный переход в поток окна.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// ffmpeg не запустился или завершился с ненулевым кодом; в сообщении — его stderr.
    /// </exception>
    public static async Task RunAsync(
        string args, long total, Action<FFmpegProgress>? progress, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(AppEnv.FFmpegExe, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        using var process = new Process { StartInfo = psi };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(Loc.Str("FFmpeg.StartFailed", AppEnv.FFmpegExe, ex.Message), ex);
        }

        // Отмена = снять процесс: ffmpeg не умеет мягко прерываться без консоли.
        using var kill = ct.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception) { /* уже завершился */ }
        });

        var errors = new StringBuilder();
        var stderr = Task.Run(async () =>
        {
            var text = await process.StandardError.ReadToEndAsync();
            errors.Append(text);
        }, CancellationToken.None);

        var started = Stopwatch.StartNew();
        long frame = 0;
        double speed = 0;

        while (await process.StandardOutput.ReadLineAsync(CancellationToken.None) is { } line)
        {
            var split = line.IndexOf('=');
            if (split <= 0) continue;

            var name = line[..split].Trim();
            var value = line[(split + 1)..].Trim();

            switch (name)
            {
                case "frame":
                    if (long.TryParse(value, out var f)) frame = f;
                    break;

                case "speed":
                    // приходит как «1.42x», на старте бывает «N/A»
                    if (double.TryParse(value.TrimEnd('x'), NumberStyles.Float, CultureInfo.InvariantCulture, out var s))
                        speed = s;
                    break;

                case "progress":
                    progress?.Invoke(Snapshot(frame, total, started.Elapsed, speed));
                    break;
            }
        }

        await process.WaitForExitAsync(CancellationToken.None);
        await stderr;

        ct.ThrowIfCancellationRequested();

        if (process.ExitCode != 0)
        {
            var message = errors.ToString().Trim();
            if (message.Length > 300) message = message[..300] + "…";
            throw new InvalidOperationException(
                string.IsNullOrEmpty(message) ? Loc.Str("FFmpeg.ExitCode", process.ExitCode) : message);
        }

        progress?.Invoke(new FFmpegProgress(100, total, total, TimeSpan.Zero, speed));
    }

    private static FFmpegProgress Snapshot(long frame, long total, TimeSpan elapsed, double speed)
    {
        var percent = total > 0 ? Math.Clamp(frame * 100.0 / total, 0, 100) : 0;

        // Оценка по фактической скорости с начала работы: у ffmpeg скорость почти
        // постоянна, а мгновенная (speed) слишком дёргается на первых секундах.
        var eta = TimeSpan.Zero;
        if (frame > 0 && total > frame && elapsed > TimeSpan.Zero)
        {
            var perFrame = elapsed.TotalSeconds / frame;
            eta = TimeSpan.FromSeconds(perFrame * (total - frame));
        }

        return new FFmpegProgress(percent, frame, total, eta, speed);
    }
}
