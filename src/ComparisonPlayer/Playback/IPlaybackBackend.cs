namespace ComparisonPlayer.Playback;

/// <summary>Результат попытки открыть файл.</summary>
/// <param name="Success">Файл открыт и готов к воспроизведению.</param>
/// <param name="Error">Причина отказа; пусто при успехе.</param>
public readonly record struct OpenResult(bool Success, string Error)
{
    public static OpenResult Ok() => new(true, "");
    public static OpenResult Fail(string error) => new(false, error);
}

/// <summary>
/// Движок воспроизведения одного ролика: всё, что UI знает о видео.
/// Единственная точка, через которую фазы 3+ подменят реализацию
/// (прямой декод FlyleafLib ↔ кадры из дискового кэша ffmpeg),
/// поэтому интерфейс не знает ни о WPF, ни о FlyleafLib.
/// </summary>
public interface IPlaybackBackend : IDisposable
{
    /// <summary>Характеристики открытого ролика; null, пока ничего не открыто.</summary>
    MediaInfo? Media { get; }

    bool IsOpen { get; }
    bool IsPlaying { get; }

    /// <summary>Текущая позиция воспроизведения.</summary>
    TimeSpan Position { get; }

    /// <summary>Номер текущего кадра, отсчёт с нуля. При VFR — производная от позиции величина.</summary>
    long FrameIndex { get; }

    /// <summary>Позиция сменилась: шаг, seek или ход воспроизведения.</summary>
    event EventHandler? PositionChanged;

    /// <summary>Сменилось состояние: файл открыт или закрыт, воспроизведение началось или встало.</summary>
    event EventHandler? StateChanged;

    OpenResult Open(string path);
    void Close();

    void Play();
    void Pause();
    void TogglePlayPause();

    /// <summary>Шаг вперёд на указанное число кадров; всегда переводит в паузу.</summary>
    void StepForward(int frames = 1);

    /// <summary>Шаг назад на указанное число кадров; всегда переводит в паузу.</summary>
    void StepBack(int frames = 1);

    /// <summary>Точный переход на кадр по номеру.</summary>
    void SeekToFrame(long frame);

    /// <summary>Точный переход на позицию; попадает на кадр, покрывающий это время.</summary>
    void SeekTo(TimeSpan position);
}
