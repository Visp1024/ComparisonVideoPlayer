using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Spike.Mpv.Bench;

/// <summary>
/// Объективная проверка точности seek. В тестовые ролики впечатан номер кадра
/// (drawtext на x=100,y=100, кегль 200), поэтому сравнивается не весь кадр — на testsrc2
/// соседние кадры почти одинаковы и полнокадровая метрика ничего не доказывает
/// (см. docs/spike-flyleaf.md, п. 2), — а только область с номером.
///
/// Снимок плеера сравнивается с эталонами кадров N-1, N, N+1, вырезанными ffmpeg;
/// «распознанным» считается тот, у которого разница минимальна. Совпадение argmin с целью
/// = seek попал ровно в запрошенный кадр.
/// </summary>
public static class FrameCompare
{
    /// <summary>Область с впечатанным номером кадра в координатах исходного видео.</summary>
    private static readonly Int32Rect NumberBox = new(80, 80, 720, 280);

    public static bool TryExtractReference(string videoPath, int frameIndex, double fps, string outPng)
    {
        if (File.Exists(outPng)) return true;
        if (!File.Exists(SpikeEnv.FFmpegExe)) return false;

        // середина кадра N по времени: точный seek ffmpeg (-ss до -i) декодирует от ключевого кадра
        var t = ((frameIndex + 0.5) / fps).ToString("F6", CultureInfo.InvariantCulture);
        var psi = new ProcessStartInfo(SpikeEnv.FFmpegExe)
        {
            Arguments = $"-y -hide_banner -loglevel error -ss {t} -i \"{videoPath}\" -frames:v 1 \"{outPng}\"",
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi)!;
        proc.StandardError.ReadToEnd();
        proc.WaitForExit(120_000);
        return File.Exists(outPng);
    }

    /// <summary>Средняя абсолютная разница яркости в области номера кадра (0..255).</summary>
    public static double NumberBoxDiff(string pngA, string pngB)
    {
        var a = LoadNumberBoxGray(pngA);
        var b = LoadNumberBoxGray(pngB);
        if (a is null || b is null || a.Length != b.Length) return double.NaN;

        double sum = 0;
        for (int i = 0; i < a.Length; i++) sum += Math.Abs(a[i] - b[i]);
        return sum / a.Length;
    }

    private static byte[]? LoadNumberBoxGray(string path)
    {
        if (!File.Exists(path)) return null;

        using var fs = File.OpenRead(path);
        var frame = BitmapFrame.Create(fs, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        if (frame.PixelWidth < NumberBox.X + NumberBox.Width || frame.PixelHeight < NumberBox.Y + NumberBox.Height)
            return null;

        var cropped = new CroppedBitmap(frame, NumberBox);
        var gray = new FormatConvertedBitmap(cropped, PixelFormats.Gray8, null, 0);

        var stride = NumberBox.Width;
        var buf = new byte[stride * NumberBox.Height];
        gray.CopyPixels(buf, stride, 0);
        return buf;
    }
}
