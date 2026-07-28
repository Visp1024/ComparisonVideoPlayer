using System.IO;

namespace ComparisonPlayer.Playback;

/// <summary>
/// Характеристики открытого ролика. Берутся у демуксера (а не у статистики
/// воспроизведения: та на паузе нулевая), поэтому доступны сразу после открытия.
/// </summary>
/// <param name="FilePath">Полный путь к файлу.</param>
/// <param name="Codec">Кодек видеодорожки, например H264.</param>
/// <param name="Width">Ширина кадра в пикселях.</param>
/// <param name="Height">Высота кадра в пикселях.</param>
/// <param name="Fps">Средняя частота кадров. При VFR — именно средняя.</param>
/// <param name="Duration">Длительность ролика.</param>
/// <param name="FrameCount">Число кадров; при VFR — оценка.</param>
/// <param name="IsVariableFrameRate">
/// Переменная частота кадров: r_frame_rate заметно отличается от avg_frame_rate.
/// При VFR номер кадра — производная от timestamp величина, а не точный счётчик.
/// </param>
/// <param name="HardwareAcceleration">Декодирование идёт на видеокарте (D3D11).</param>
/// <param name="FromCache">
/// Кадры берутся из all-intra прокси, собранного ffmpeg'ом (фаза 4), а не из исходного файла.
/// Остальные поля при этом описывают исходник — кроме частоты и числа кадров, которые
/// прокси приводит к постоянным.
/// </param>
public sealed record MediaInfo(
    string FilePath,
    string Codec,
    int Width,
    int Height,
    double Fps,
    TimeSpan Duration,
    long FrameCount,
    bool IsVariableFrameRate,
    bool HardwareAcceleration,
    bool FromCache = false)
{
    public string FileName => Path.GetFileName(FilePath);

    /// <summary>Длительность одного кадра при средней частоте; ноль, если частота неизвестна.</summary>
    public TimeSpan FrameDuration => Fps > 0 ? TimeSpan.FromSeconds(1 / Fps) : TimeSpan.Zero;

    /// <summary>
    /// Номер кадра для позиции. При VFR результат приблизительный —
    /// точной привязки номера к timestamp у ролика с переменной частотой нет.
    /// </summary>
    public long FrameAt(TimeSpan position)
    {
        if (Fps <= 0) return 0;
        var frame = (long)Math.Round(position.TotalSeconds * Fps, MidpointRounding.AwayFromZero);
        return Math.Clamp(frame, 0, Math.Max(FrameCount - 1, 0));
    }
}
