using System.Diagnostics;
using System.IO;

namespace Spike.Flyleaf.Bench;

/// <summary>
/// Параметры файла по ffprobe. Нужен потому, что Player.Video.Width/Height у FlyleafLib
/// на паузе остаются нулевыми (статистика видео обновляется таймером UI только при воспроизведении),
/// а в отчёте нужны настоящие характеристики ролика.
/// </summary>
public static class MediaProbe
{
    public static string Describe(string file)
    {
        var exe = Path.Combine(SpikeEnv.FFmpegDir, "ffprobe.exe");
        if (!File.Exists(exe)) return "(ffprobe недоступен)";

        var psi = new ProcessStartInfo(exe)
        {
            Arguments = "-v error -select_streams v:0 -show_entries stream=codec_name,width,height,r_frame_rate,nb_frames " +
                        $"-of default=noprint_wrappers=1:nokey=1 \"{file}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi)!;
        var lines = proc.StandardOutput.ReadToEnd().Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        proc.WaitForExit(30_000);
        if (lines.Length < 5) return "(ffprobe не дал данных)";

        var fps = lines[3].Trim().Split('/') is [var n, var d] && double.TryParse(d, out var den) && den != 0
            ? (double.Parse(n) / den).ToString("F3")
            : lines[3].Trim();

        return $"{lines[0].Trim()} {lines[1].Trim()}x{lines[2].Trim()} {fps} fps, кадров {lines[4].Trim()}";
    }
}
