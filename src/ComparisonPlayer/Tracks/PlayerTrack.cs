using System.Windows.Media;
using ComparisonPlayer.Cache;
using ComparisonPlayer.Playback;

namespace ComparisonPlayer.Tracks;

/// <summary>Какой из двух треков сравнения.</summary>
public enum TrackId
{
    A,
    B
}

/// <summary>
/// Один трек сравнения: свой движок воспроизведения, свой сдвиг относительно
/// мастер-клока, свой отрезок in/out и своё состояние кэша кадров (фаза 4).
/// </summary>
/// <remarks>
/// До фазы 3 всё это лежало полями в <see cref="MainWindow"/> — с двумя треками
/// поля пришлось бы удваивать поимённо. Теперь окно держит два таких объекта,
/// а <see cref="SyncEngine"/> сводит их под один транспорт.
/// </remarks>
public sealed class PlayerTrack : IDisposable
{
    public PlayerTrack(TrackId id)
    {
        Id = id;
        Backend = Flyleaf;
    }

    public TrackId Id { get; }

    /// <summary>Буква трека: ею он подписан и в кадре, и на таймлайне.</summary>
    public string Letter => Id == TrackId.A ? "A" : "B";

    /// <summary>
    /// Прямой декод: он же владеет Player'ом, привязанным к своему FlyleafHost.
    /// Живёт всё время работы окна, даже когда кадры идут из кэша.
    /// </summary>
    public FlyleafBackend Flyleaf { get; } = new();

    /// <summary>Действующий движок трека: прямой декод либо кэш кадров поверх него.</summary>
    public IPlaybackBackend Backend { get; set; }

    public MediaInfo? Media => Backend.Media;
    public bool IsOpen => Backend.IsOpen;

    public double Fps => Media is { Fps: > 0 } m ? m.Fps : 0;
    public long FrameCount => Media?.FrameCount ?? 0;
    public long LastFrame => Math.Max(FrameCount - 1, 0);
    public TimeSpan Duration => Media?.Duration ?? TimeSpan.Zero;

    // ---------- выравнивание ----------

    /// <summary>
    /// Сдвиг трека относительно мастер-клока: позиция трека —
    /// <c>master_time − Offset</c>. Всегда неотрицателен: движок нормализует
    /// пару треков так, чтобы более ранний стоял в нуле линейки.
    /// </summary>
    public TimeSpan Offset { get; set; }

    /// <summary>Сдвиг в кадрах этого трека — им подписывают выравнивание в интерфейсе.</summary>
    public long OffsetFrames => Fps > 0 ? (long)Math.Round(Offset.TotalSeconds * Fps) : 0;

    // ---------- отрезок воспроизведения ----------

    /// <summary>Первый кадр отрезка (в кадрах самого трека).</summary>
    public long InFrame { get; private set; }

    /// <summary>Последний кадр отрезка, входит в него (в кадрах самого трека).</summary>
    public long OutFrame { get; private set; }

    public bool IsFullSegment => InFrame == 0 && OutFrame == LastFrame;
    public long SegmentFrames => OutFrame - InFrame + 1;

    public void SetIn(long frame)
    {
        InFrame = Math.Clamp(frame, 0, LastFrame);
        if (OutFrame < InFrame) OutFrame = InFrame;
    }

    public void SetOut(long frame)
    {
        OutFrame = Math.Clamp(frame, 0, LastFrame);
        if (InFrame > OutFrame) InFrame = OutFrame;
    }

    public void ResetSegment()
    {
        InFrame = 0;
        OutFrame = LastFrame;
    }

    // ---------- пересчёт времени ----------

    /// <summary>Время кадра этого трека.</summary>
    public TimeSpan TimeOf(long frame) => Fps > 0 ? TimeSpan.FromSeconds(frame / Fps) : TimeSpan.Zero;

    /// <summary>Кадр трека, покрывающий его локальное время.</summary>
    public long FrameAt(TimeSpan local) =>
        Fps > 0 ? (long)Math.Round(local.TotalSeconds * Fps) : 0;

    /// <summary>Локальное время трека для времени мастер-клока.</summary>
    public TimeSpan LocalTime(TimeSpan masterTime) => masterTime - Offset;

    /// <summary>
    /// Есть ли у трека кадр в этот момент мастер-времени: за краями материала
    /// и вне отрезка кадра нет — панель показывает «нет кадра», а не соседний кадр.
    /// </summary>
    public bool HasFrameAt(TimeSpan masterTime)
    {
        if (!IsOpen) return false;

        var frame = FrameAt(LocalTime(masterTime));
        return frame >= InFrame && frame <= OutFrame;
    }

    /// <summary>Полная длина трека на общей шкале: сдвиг плюс собственная длительность.</summary>
    public TimeSpan EndOnMaster => Offset + (Fps > 0 ? TimeSpan.FromSeconds(FrameCount / Fps) : Duration);

    // ---------- состояние кэша кадров (фаза 4) ----------

    /// <summary>Ключ кэша открытого файла.</summary>
    public string? CacheKey { get; set; }

    /// <summary>Ключ запомненного замера шага назад.</summary>
    public string? ProbeKey { get; set; }

    /// <summary>Готовая запись кэша открытого файла, если она есть.</summary>
    public CacheEntry? CacheEntry { get; set; }

    /// <summary>Замер шага назад на исходнике, мс; 0 — не мерили.</summary>
    public double SourceStepMs { get; set; }

    /// <summary>Замер шага назад на прокси, мс; 0 — не мерили.</summary>
    public double CacheStepMs { get; set; }

    /// <summary>Идущая сборка кэша этого трека; null — сборки нет.</summary>
    public CancellationTokenSource? BuildCts { get; set; }

    public double BuildPercent { get; set; }

    /// <summary>Оценка оставшегося времени сборки; пусто — оценки нет.</summary>
    public string BuildEta { get; set; } = "";

    /// <summary>Какая доля ролика уже в кэше.</summary>
    public double BuiltFraction { get; set; }

    /// <summary>Сколько кадров прокси уже записано ffmpeg'ом; 0 — сборки нет.</summary>
    public long BuiltFrames { get; set; }

    /// <summary>Все снятые миниатюры файла.</summary>
    public IReadOnlyList<string> ThumbFiles { get; set; } = [];

    /// <summary>Шаг между миниатюрами по времени, секунд; 0 — плана нет.</summary>
    public double ThumbInterval { get; set; }

    /// <summary>Декодированные миниатюры: клип перерисовывается на каждый зум и шаг.</summary>
    public Dictionary<string, ImageSource> ThumbImages { get; } = [];

    /// <summary>Есть ли что показывать в клипе вместо заливки.</summary>
    public bool HasThumbnails { get; set; }

    /// <summary>Забыть всё, что относилось к прошлому файлу этого трека.</summary>
    public void ResetCacheState()
    {
        CacheKey = null;
        ProbeKey = null;
        CacheEntry = null;
        SourceStepMs = 0;
        CacheStepMs = 0;
        BuildPercent = 0;
        BuildEta = "";
        BuiltFrames = 0;
        BuiltFraction = 0;
        ThumbFiles = [];
        ThumbInterval = 0;
        ThumbImages.Clear();
        HasThumbnails = false;
    }

    public void Dispose()
    {
        if (!ReferenceEquals(Backend, Flyleaf)) Backend.Dispose();
        Flyleaf.Dispose();
    }
}
