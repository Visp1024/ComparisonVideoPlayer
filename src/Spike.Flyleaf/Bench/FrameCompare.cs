using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Spike.Flyleaf.Bench;

/// <summary>
/// Проверка точности seek «по картинке»: эталонный кадр N вынимается из файла через ffmpeg CLI,
/// сравнивается со снимком экрана плеера. Средняя абсолютная разница (0..255) по серому.
/// Совпадение кадров даёт единицы (артефакты масштабирования), промах на кадр — десятки.
/// </summary>
public static class FrameCompare
{
    private const int W = 640;
    private const int H = 360;

    public static bool TryExtractReference(string videoPath, int frameIndex, string outPng, out string error)
    {
        error = "";
        if (!File.Exists(SpikeEnv.FFmpegExe))
        {
            error = "ffmpeg.exe не найден";
            return false;
        }

        var psi = new ProcessStartInfo(SpikeEnv.FFmpegExe)
        {
            // select по номеру кадра требует декода с начала — медленно, но это эталон, не замер
            Arguments = $"-y -hide_banner -loglevel error -i \"{videoPath}\" " +
                        $"-vf \"select=eq(n\\,{frameIndex}),scale={W}:{H}\" -vsync 0 -frames:v 1 \"{outPng}\"",
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi)!;
        error = proc.StandardError.ReadToEnd().Trim();
        proc.WaitForExit(120_000);
        return File.Exists(outPng);
    }

    /// <summary>Средняя абсолютная разница яркости двух PNG, приведённых к одному размеру.</summary>
    public static double MeanAbsDiff(string pngA, string pngB)
    {
        var a = LoadGray(pngA);
        var b = LoadGray(pngB);
        var n = Math.Min(a.Length, b.Length);
        if (n == 0) return double.NaN;

        double sum = 0;
        for (int i = 0; i < n; i++) sum += Math.Abs(a[i] - b[i]);
        return sum / n;
    }

    private static byte[] LoadGray(string path)
    {
        using var fs = File.OpenRead(path);
        var frame = BitmapFrame.Create(fs, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var scaled = new TransformedBitmap(frame, new ScaleTransform((double)W / frame.PixelWidth, (double)H / frame.PixelHeight));
        var gray = new FormatConvertedBitmap(scaled, PixelFormats.Gray8, null, 0);

        var stride = W;
        var buf = new byte[stride * H];
        gray.CopyPixels(buf, stride, 0);
        return buf;
    }
}
