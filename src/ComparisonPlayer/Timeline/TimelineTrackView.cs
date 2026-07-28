using System.Windows.Media;

namespace ComparisonPlayer.Timeline;

/// <summary>
/// Дорожка таймлайна глазами контрола: где на общей шкале лежит клип, где его отрезок
/// и чем он закрашен. Всё уже переведено в кадры мастер-клока — контрол не знает
/// ни про fps треков, ни про сдвиги.
/// </summary>
public sealed class TimelineTrackView
{
    /// <summary>Буква трека в ярлыке дорожки.</summary>
    public required string Letter { get; init; }

    /// <summary>Трек ведёт мастер-клок — отмечается звёздочкой.</summary>
    public bool IsMaster { get; init; }

    /// <summary>Активный трек: ему адресованы I/O, «Сбросить отрезок» и открытие файла.</summary>
    public bool IsActive { get; init; }

    /// <summary>Пустая дорожка рисуется приглушённой и ничего не принимает.</summary>
    public bool IsOpen { get; init; }

    /// <summary>Первый кадр клипа на общей шкале (он же сдвиг трека).</summary>
    public long StartFrame { get; init; }

    /// <summary>Кадр общей шкалы сразу за концом клипа.</summary>
    public long EndFrame { get; init; }

    /// <summary>Границы отрезка воспроизведения, приведённые к общей шкале.</summary>
    public long InFrame { get; init; }

    public long OutFrame { get; init; }

    /// <summary>Доля клипа, собранная в кэш кадров (фаза 4).</summary>
    public double BuiltFraction { get; init; }

    public bool HasThumbnails { get; init; }

    /// <summary>Соотношение сторон кадра — по нему считается ширина клетки миниатюр.</summary>
    public double Aspect { get; init; } = 16 / 9.0;

    /// <summary>Миниатюра для <b>локального</b> момента ролика; null — кадр ещё не снят.</summary>
    public Func<TimeSpan, ImageSource?>? ThumbnailProvider { get; init; }

    /// <summary>Локальное время клипа для кадра общей шкалы.</summary>
    public TimeSpan LocalTime(double timelineFrame, double masterFps) =>
        masterFps > 0 ? TimeSpan.FromSeconds(Math.Max(timelineFrame - StartFrame, 0) / masterFps) : TimeSpan.Zero;
}
