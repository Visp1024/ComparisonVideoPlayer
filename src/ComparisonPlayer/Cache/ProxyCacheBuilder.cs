using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using ComparisonPlayer.Playback;

namespace ComparisonPlayer.Cache;

/// <summary>Что именно снимает ffmpeg: прокси кэша или полоску превью.</summary>
public enum BuildStage
{
    Proxy,
    Thumbnails
}

/// <param name="Stage">Что именно строится сейчас.</param>
/// <param name="Percent">Готовность этапа, 0..100.</param>
/// <param name="Frame">Обработано кадров (для миниатюр — сколько их уже записано).</param>
/// <param name="Total">Сколько кадров всего ожидается на этапе.</param>
/// <param name="Eta">Оценка оставшегося времени этапа.</param>
/// <param name="Speed">Скорость относительно реального времени, как её сообщает ffmpeg.</param>
public readonly record struct BuildProgress(
    BuildStage Stage, double Percent, long Frame, long Total, TimeSpan Eta, double Speed);

/// <summary>
/// Сборка через ffmpeg CLI: all-intra прокси кэша и полоска превью для таймлайна.
/// Это две независимые записи с разными ключами — превью нужны и там, где прокси
/// не собирается вовсе, поэтому одна не является этапом другой.
/// </summary>
public sealed class ProxyCacheBuilder(FrameCacheStore store)
{
    /// <summary>Версия набора параметров кодирования; меняется вместе с ними.</summary>
    public const string Version = "h264-intra-crf20-yuv420p-v1";

    /// <summary>
    /// Подпись параметров полоски превью — она же ключ записи с ними. От параметров
    /// прокси не зависит намеренно: превью одного и того же файла одинаковы при любой
    /// частоте кэша и при выключенном кэше, пересниматься из-за неё им незачем.
    /// </summary>
    public const string ThumbnailVersion = "thumbs-h72-v1";

    /// <summary>
    /// Версия звуковой дорожки прокси. Держится отдельно от <see cref="Version"/> и в
    /// ключ кэша не входит: кадры от неё не зависят, поэтому записи, собранные до
    /// появления звука, остаются годными — по этому полю видно, что они молчат.
    /// </summary>
    public const string AudioVersion = "aac-192k-v1";

    /// <summary>
    /// Подпись параметров прокси. Входит в ключ кэша: меняем параметры или частоту —
    /// старые записи перестают подходить сами, без ручной инвалидации.
    /// </summary>
    /// <param name="fps">Частота прокси; 0 — как в исходнике.</param>
    public static string Signature(double fps) =>
        fps > 0 ? $"{Version}-fps{fps.ToString("0.###", CultureInfo.InvariantCulture)}" : Version;

    /// <summary>Имя файла прокси внутри папки записи.</summary>
    public const string ProxyFile = "proxy.mp4";

    private const int Crf = 20;
    private const int ThumbnailHeight = 72;
    private const int MinThumbnails = 12;
    private const int MaxThumbnails = 240;

    /// <summary>
    /// Собрать запись кэша для открытого ролика.
    /// </summary>
    /// <param name="media">Сведения об исходнике (нужны частота и число кадров для прогресса).</param>
    /// <param name="key">Ключ записи — он же имя папки.</param>
    /// <param name="targetFps">Частота прокси; 0 — как в исходнике.</param>
    public async Task<CacheEntry> BuildAsync(
        MediaInfo media, string key, double targetFps,
        IProgress<BuildProgress>? progress, CancellationToken ct)
    {
        var dir = store.DirectoryFor(key);
        Directory.CreateDirectory(dir);

        // Прокси пишется сразу под своим именем, без временного файла и переименования:
        // плеер играет его прямо во время сборки и держит открытым, а переименовать
        // занятый файл нельзя. Признак готовности — entry.json, он появляется в конце.
        var proxy = Path.Combine(dir, ProxyFile);
        SafeDelete(Path.Combine(dir, "entry.json"));

        var fps = targetFps > 0 ? targetFps : media.Fps;
        var totalFrames = ExpectedFrames(media, fps);

        await RunAsync(ProxyArgs(media, fps, proxy), totalFrames, BuildStage.Proxy, progress, ct);

        var entry = new CacheEntry
        {
            Key = key,
            Directory = dir,
            SourcePath = media.FilePath,
            SourceLength = SafeLength(media.FilePath),
            Codec = media.Codec,
            Width = media.Width,
            Height = media.Height,
            SourceFps = media.Fps,
            Fps = fps,
            FrameCount = totalFrames,
            DurationTicks = media.Duration.Ticks,
            Parameters = Signature(targetFps),
            AudioVersion = AudioVersion,
            CreatedUtc = DateTime.UtcNow,
            LastUsedUtc = DateTime.UtcNow
        };

        store.Save(entry);
        return entry;
    }

    /// <summary>
    /// Снять полоску превью отдельной записью, без прокси. Так они появляются в любом
    /// режиме — в том числе при выключенном кэше, где иначе клип на таймлайне остаётся
    /// пустым. Запись сохраняется в конце: недоснятая полоска не должна выглядеть готовой.
    /// </summary>
    /// <param name="media">Исходник, с которого снимаем кадры.</param>
    /// <param name="key">Ключ записи с превью — он же имя папки.</param>
    public async Task<CacheEntry> BuildThumbnailsOnlyAsync(
        MediaInfo media, string key, IProgress<BuildProgress>? progress, CancellationToken ct)
    {
        var dir = store.DirectoryFor(key);
        Directory.CreateDirectory(dir);
        SafeDelete(Path.Combine(dir, "entry.json"));

        var (count, interval) = await BuildThumbnailsAsync(media, media.FrameCount, dir, progress, ct);

        var entry = new CacheEntry
        {
            Key = key,
            Directory = dir,
            SourcePath = media.FilePath,
            SourceLength = SafeLength(media.FilePath),
            Codec = media.Codec,
            Width = media.Width,
            Height = media.Height,
            SourceFps = media.Fps,
            Fps = media.Fps,
            FrameCount = media.FrameCount,
            DurationTicks = media.Duration.Ticks,
            ProxyFile = "",
            ThumbnailsOnly = true,
            ThumbnailCount = count,
            ThumbnailIntervalSeconds = interval,
            Parameters = ThumbnailVersion,
            CreatedUtc = DateTime.UtcNow,
            LastUsedUtc = DateTime.UtcNow
        };

        store.Save(entry);
        return entry;
    }

    /// <summary>
    /// Сколько миниатюр снимаем и с каким шагом по времени. Считается и при сборке,
    /// и при показе: полоска раскладывает кадры по их местам на шкале времени, а не
    /// растягивает готовые на всю ширину — иначе во время сборки она врала бы о том,
    /// какой кусок ролика уже разобран.
    /// </summary>
    public static (int Count, double Interval) ThumbnailPlan(TimeSpan duration, long frames)
    {
        var seconds = duration.TotalSeconds;
        if (seconds <= 0) return (0, 0);

        var count = (int)Math.Clamp(Math.Round(seconds / 2), MinThumbnails, MaxThumbnails);
        count = (int)Math.Min(count, Math.Max(frames, 1));
        return (count, seconds / count);
    }

    /// <summary>Сколько кадров окажется в прокси при заданной частоте.</summary>
    private static long ExpectedFrames(MediaInfo media, double fps)
    {
        if (fps <= 0 || media.Duration <= TimeSpan.Zero)
            return Math.Max(media.FrameCount, 1);

        return Math.Max((long)Math.Round(media.Duration.TotalSeconds * fps), 1);
    }

    /// <summary>
    /// Прокси: каждый кадр — ключевой (<c>-g 1</c>), поэтому шаг назад не требует
    /// декодирования всей группы кадров. Частота приводится к постоянной — заодно
    /// это нормализует VFR-исходники, у которых номер кадра иначе неоднозначен.
    ///
    /// Звук переносится в прокси (задача #20): играет-то прокси, и брать звук из
    /// исходника значило бы завести второй декодер, который пришлось бы догонять
    /// до кадра — а так дорожка идёт тем же демуксером и не расходится в принципе.
    /// Пониженная частота прокси на неё не влияет: <c>-r</c> касается только видео.
    /// </summary>
    private static string ProxyArgs(MediaInfo media, double fps, string output)
    {
        var rate = fps > 0
            ? $" -r {fps.ToString("0.######", CultureInfo.InvariantCulture)}"
            : "";

        // Фрагментированный MP4 (frag_keyframe + empty_moov): файл читается ещё
        // до окончания записи, поэтому плеер играет уже собранную часть кэша.
        // Обычный MP4 дописывает оглавление в конце и до этого непригоден.
        // Звук пересобираем в AAC, а не копируем: mp4 принимает не всякий кодек
        // (тот же Vorbis из mkv в него не уложить), а «?» у дорожки означает
        // «если она есть» — ролик без звука так и остаётся немым, а не отказом.
        return $"-hide_banner -nostdin -loglevel error -y -i \"{media.FilePath}\" " +
               $"-map 0:v:0 -map 0:a:0? -sn -dn " +
               $"-c:a aac -b:a 192k " +
               $"-c:v libx264 -preset veryfast -crf {Crf} " +
               $"-g 1 -keyint_min 1 -sc_threshold 0 -bf 0 " +
               $"-pix_fmt yuv420p -fps_mode cfr{rate} " +
               $"-movflags +frag_keyframe+empty_moov+default_base_moof -frag_duration 1000000 " +
               $"-progress pipe:1 -nostats \"{output}\"";
    }

    /// <summary>
    /// Миниатюры снимаются с исходника одним проходом ffmpeg. Кадров берём столько,
    /// чтобы хватило на ширину полосы и не больше: секундный шаг на часовом ролике
    /// дал бы 3600 файлов.
    /// </summary>
    private static async Task<(int Count, double Interval)> BuildThumbnailsAsync(
        MediaInfo media, long frames, string dir, IProgress<BuildProgress>? progress, CancellationToken ct)
    {
        var (count, interval) = ThumbnailPlan(media.Duration, frames);
        if (count == 0) return (0, 0);

        var thumbs = Path.Combine(dir, "thumbs");
        if (Directory.Exists(thumbs)) Directory.Delete(thumbs, recursive: true);
        Directory.CreateDirectory(thumbs);

        var fps = (1 / interval).ToString("0.######", CultureInfo.InvariantCulture);
        var args = $"-hide_banner -nostdin -loglevel error -y -i \"{media.FilePath}\" " +
                   $"-map 0:v:0 -vf \"fps={fps},scale=-2:{ThumbnailHeight}\" -q:v 4 " +
                   $"-progress pipe:1 -nostats \"{Path.Combine(thumbs, "%05d.jpg")}\"";

        await RunAsync(args, count, BuildStage.Thumbnails, progress, ct);

        return (Directory.GetFiles(thumbs, "*.jpg").Length, interval);
    }

    /// <summary>
    /// Запуск ffmpeg с разбором блоков <c>-progress</c>. Блок приходит построчно
    /// парами «ключ=значение» и заканчивается строкой <c>progress=</c>, по ней и
    /// сообщаем прогресс наружу.
    /// </summary>
    private static async Task RunAsync(
        string args, long total, BuildStage stage, IProgress<BuildProgress>? progress, CancellationToken ct)
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
            throw new InvalidOperationException($"не удалось запустить ffmpeg ({AppEnv.FFmpegExe}): {ex.Message}", ex);
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
                    progress?.Report(Snapshot(stage, frame, total, started.Elapsed, speed));
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
                string.IsNullOrEmpty(message) ? $"ffmpeg завершился с кодом {process.ExitCode}" : message);
        }

        progress?.Report(new BuildProgress(stage, 100, total, total, TimeSpan.Zero, speed));
    }

    private static BuildProgress Snapshot(BuildStage stage, long frame, long total, TimeSpan elapsed, double speed)
    {
        var percent = total > 0 ? Math.Clamp(frame * 100.0 / total, 0, 100) : 0;

        // Оценка по фактической скорости с начала этапа: у ffmpeg скорость почти
        // постоянна, а мгновенная (speed) слишком дёргается на первых секундах.
        var eta = TimeSpan.Zero;
        if (frame > 0 && total > frame && elapsed > TimeSpan.Zero)
        {
            var perFrame = elapsed.TotalSeconds / frame;
            eta = TimeSpan.FromSeconds(perFrame * (total - frame));
        }

        return new BuildProgress(stage, percent, frame, total, eta, speed);
    }

    private static void SafeDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception) { /* занятый файл переживём: перезапишем следующей сборкой */ }
    }

    private static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; }
        catch (Exception) { return 0; }
    }
}
