using System.Windows.Threading;
using ComparisonPlayer.Playback;

namespace ComparisonPlayer.Tracks;

/// <summary>
/// Мастер-клок и транспорт двух треков (PLAN.md §4.2). Все транспортные команды идут
/// сюда, а не в плеер: движок сам раздаёт их обоим трекам и следит за расхождением.
/// </summary>
/// <remarks>
/// Позиция трека — <c>master_time − offset</c>, приведённая к ближайшему кадру трека:
/// при разных fps один и тот же кадр ведомого может держаться два шага мастера подряд,
/// и это правильное поведение, а не рассинхрон. Шаг и seek выполняются точным переходом
/// обоих треков, поэтому на паузе рассинхрона нет по определению; расходиться могут
/// только два независимо играющих декодера — за этим следит <see cref="Drift"/>.
/// </remarks>
public sealed class SyncEngine : IDisposable
{
    /// <summary>Как часто сверять ведомого с мастером при воспроизведении.</summary>
    private static readonly TimeSpan DriftCheckInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Расхождение часов, с которого ведомого подтягивают, в его кадрах. Полкадра —
    /// слишком чутко: подтягивание идёт точным seek, и на каждой мелочи оно само
    /// сбивало бы вывод. Полтора кадра — расхождение, которое уже видно глазом.
    /// </summary>
    private const double DriftFramesThreshold = 1.5;

    private readonly DispatcherTimer _driftTimer;
    private bool _disposed;

    public SyncEngine(PlayerTrack a, PlayerTrack b)
    {
        A = a;
        B = b;

        _driftTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = DriftCheckInterval };
        _driftTimer.Tick += (_, _) => CorrectDrift();
        _driftTimer.Start();

        Rebind();
    }

    public PlayerTrack A { get; }
    public PlayerTrack B { get; }

    public IEnumerable<PlayerTrack> Tracks => [A, B];
    public IEnumerable<PlayerTrack> OpenTracks => Tracks.Where(t => t.IsOpen);

    /// <summary>Трек, кадрами которого меряется шаг. Назначается пользователем (клавиша M).</summary>
    public TrackId MasterId { get; private set; } = TrackId.A;

    /// <summary>
    /// Трек, который на самом деле ведёт клок: назначенный мастер, если он открыт,
    /// иначе единственный открытый. Пока не открыт ни один — null.
    /// </summary>
    public PlayerTrack? Master =>
        Track(MasterId) is { IsOpen: true } master ? master : OpenTracks.FirstOrDefault();

    public PlayerTrack? Slave => Master is { } m ? Other(m) : null;

    public PlayerTrack Track(TrackId id) => id == TrackId.A ? A : B;
    public PlayerTrack Other(PlayerTrack track) => ReferenceEquals(track, A) ? B : A;

    public bool IsOpen => Master is not null;
    public bool IsPlaying => Master?.Backend.IsPlaying ?? false;

    /// <summary>Частота мастер-клока: в этих кадрах живут playhead, зум и шаг.</summary>
    public double MasterFps => Master is { Fps: > 0 } m ? m.Fps : 0;

    /// <summary>Позиция мастер-клока, кадров от начала общей шкалы.</summary>
    public long PositionFrame { get; private set; }

    public TimeSpan PositionTime => FrameTime(PositionFrame);

    /// <summary>Длина общей шкалы в кадрах мастера: самый поздний конец из двух треков.</summary>
    public long TimelineFrames
    {
        get
        {
            if (MasterFps <= 0) return 0;

            var end = OpenTracks.Select(t => t.EndOnMaster).DefaultIfEmpty(TimeSpan.Zero).Max();
            return Math.Max((long)Math.Round(end.TotalSeconds * MasterFps), 1);
        }
    }

    public long LastFrame => Math.Max(TimelineFrames - 1, 0);

    public TimeSpan Duration => FrameTime(TimelineFrames);

    /// <summary>Начало отрезка воспроизведения на общей шкале (отрезок задаёт мастер).</summary>
    public long SegmentInFrame => Master is { } m ? ToTimeline(m, m.InFrame) : 0;

    /// <summary>Конец отрезка воспроизведения на общей шкале.</summary>
    public long SegmentOutFrame => Master is { } m ? ToTimeline(m, m.OutFrame) : 0;

    private double _speed = 1;

    /// <summary>Скорость воспроизведения; задаётся обоим трекам одинаковой.</summary>
    public double Speed
    {
        get => _speed;
        set
        {
            _speed = value;
            ApplySpeed();
        }
    }

    /// <summary>
    /// Навязать выбранную скорость движкам. Нужно и после смены бэкенда: при переходе
    /// на кэш и обратно движок подменяется и о выбранной скорости не знает.
    /// </summary>
    public void ApplySpeed()
    {
        foreach (var track in OpenTracks)
            if (Math.Abs(track.Backend.Speed - _speed) > 0.001)
                track.Backend.Speed = _speed;
    }

    // ---------- звук ----------

    private int _volume = 100;
    private bool _muted;

    /// <summary>Громкость звучащего трека, 0..100.</summary>
    public int Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0, 100);
            ApplyAudio();
        }
    }

    /// <summary>Звук выключен целиком: не звучит и мастер.</summary>
    public bool Muted
    {
        get => _muted;
        set
        {
            _muted = value;
            ApplyAudio();
        }
    }

    /// <summary>
    /// Трек, с которого идёт звук: всегда мастер (PLAN.md §7.1). Отдельного выбора нет
    /// намеренно — мастер и так тот трек, по которому идёт просмотр.
    /// </summary>
    public PlayerTrack? AudioTrack => Master;

    /// <summary>Есть ли что слушать: у звучащего трека нашлась звуковая дорожка.</summary>
    public bool HasAudio => Master is { } m && m.Backend.HasAudio;

    /// <summary>
    /// Развести звук по трекам: мастер звучит, ведомый приглушён. Нужно и после смены
    /// движка — при переходе на кэш и обратно движок подменяется и о выбранной
    /// громкости не знает (та же история, что с <see cref="ApplySpeed"/>).
    /// </summary>
    public void ApplyAudio()
    {
        foreach (var track in Tracks)
        {
            track.Backend.Volume = _volume;
            track.Backend.Muted = _muted || Master is not { } master || !ReferenceEquals(master, track);
        }
    }

    /// <summary>Последнее замеренное расхождение ведомого, мс; 0 — сверять нечего.</summary>
    public double Drift { get; private set; }

    public event EventHandler? PositionChanged;
    public event EventHandler? StateChanged;

    // ---------- пересчёт координат ----------

    /// <summary>Время мастер-клока для кадра общей шкалы.</summary>
    public TimeSpan FrameTime(long frame) =>
        MasterFps > 0 ? TimeSpan.FromSeconds(frame / MasterFps) : TimeSpan.Zero;

    /// <summary>Кадр общей шкалы для момента мастер-времени.</summary>
    public long TimelineFrameAt(TimeSpan time) =>
        MasterFps > 0 ? (long)Math.Round(time.TotalSeconds * MasterFps) : 0;

    /// <summary>Кадр трека, который стоит на этом кадре общей шкалы (без обрезки по отрезку).</summary>
    public long LocalFrame(PlayerTrack track, long timelineFrame) =>
        track.FrameAt(track.LocalTime(FrameTime(timelineFrame)));

    /// <summary>Обратный пересчёт: где на общей шкале стоит кадр трека.</summary>
    public long ToTimeline(PlayerTrack track, long localFrame) =>
        TimelineFrameAt(track.TimeOf(localFrame) + track.Offset);

    /// <summary>Номер кадра трека для показа в интерфейсе; null — кадра здесь нет.</summary>
    public long? DisplayFrame(PlayerTrack track)
    {
        if (!track.IsOpen) return null;

        var frame = LocalFrame(track, PositionFrame);
        return frame < track.InFrame || frame > track.OutFrame ? null : frame;
    }

    // ---------- подписки ----------

    /// <summary>
    /// Переустановить подписки на движки треков. Нужно после смены бэкенда
    /// (переход на кэш кадров и обратно): объект движка при этом другой.
    /// </summary>
    public void Rebind()
    {
        foreach (var track in Tracks)
        {
            track.Flyleaf.PositionChanged -= OnTrackPosition;
            track.Flyleaf.StateChanged -= OnTrackState;
            track.Backend.PositionChanged -= OnTrackPosition;
            track.Backend.StateChanged -= OnTrackState;

            track.Backend.PositionChanged += OnTrackPosition;
            track.Backend.StateChanged += OnTrackState;
        }

        ApplyAudio();
    }

    private void OnTrackPosition(object? sender, EventArgs e)
    {
        // Позицию ведёт только мастер: у ведомого она производная, и его события
        // иначе дёргали бы playhead назад-вперёд между двумя частотами.
        if (Master is not { } master || !ReferenceEquals(sender, master.Backend)) return;

        if (master.Backend.IsPlaying)
            PositionFrame = Math.Clamp(ToTimeline(master, master.Backend.FrameIndex), 0, LastFrame);

        PositionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnTrackState(object? sender, EventArgs e) => StateChanged?.Invoke(this, EventArgs.Empty);

    // ---------- выравнивание ----------

    /// <summary>
    /// Сдвинуть трек по общей шкале на указанное число кадров мастера.
    /// Пара нормализуется так, чтобы более ранний трек стоял в нуле линейки —
    /// иначе материал уезжал бы за левый край, где у шкалы нет времени.
    /// </summary>
    /// <returns>На сколько кадров мастера сдвинулось начало шкалы (playhead двигают на столько же).</returns>
    public long ShiftTrack(PlayerTrack track, long masterFrames)
    {
        if (MasterFps <= 0 || masterFrames == 0) return 0;

        track.Offset += TimeSpan.FromSeconds(masterFrames / MasterFps);
        return Normalize();
    }

    /// <summary>Сдвиг трека относительно другого, в кадрах мастера.</summary>
    public long RelativeOffsetFrames(PlayerTrack track) =>
        TimelineFrameAt(track.Offset) - TimelineFrameAt(Other(track).Offset);

    /// <summary>Убрать сдвиг: оба трека начинаются в нуле шкалы.</summary>
    public long ResetOffsets()
    {
        var shift = -TimelineFrameAt(OpenTracks.Select(t => t.Offset).DefaultIfEmpty(TimeSpan.Zero).Min());

        foreach (var track in Tracks) track.Offset = TimeSpan.Zero;
        return shift;
    }

    /// <summary>
    /// Прижать пару к нулю шкалы: у самого раннего трека сдвиг ноль, у второго —
    /// разница между ними. Возвращает, насколько при этом уехало мастер-время.
    /// </summary>
    private long Normalize()
    {
        var min = OpenTracks.Select(t => t.Offset).DefaultIfEmpty(TimeSpan.Zero).Min();
        if (min == TimeSpan.Zero) return 0;

        foreach (var track in Tracks)
            track.Offset = track.Offset - min < TimeSpan.Zero ? TimeSpan.Zero : track.Offset - min;

        return -TimelineFrameAt(min);
    }

    // ---------- транспорт ----------

    public void SetMaster(TrackId id)
    {
        if (MasterId == id) return;

        var time = PositionTime;
        MasterId = id;

        // Кадр мастера сменился — время сохраняем, номер кадра пересчитываем.
        PositionFrame = Math.Clamp(TimelineFrameAt(time), 0, LastFrame);

        // Звук идёт за мастером: иначе после смены мастера звучал бы ведомый.
        ApplyAudio();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Перевести оба трека на кадр общей шкалы.</summary>
    public void SeekToFrame(long frame, Action<PlayerTrack, long>? seekTrack = null)
    {
        if (!IsOpen) return;

        PositionFrame = Math.Clamp(frame, 0, LastFrame);

        foreach (var track in OpenTracks)
        {
            var local = Math.Clamp(LocalFrame(track, PositionFrame), 0, track.LastFrame);

            // Кто именно выполняет seek, решает окно: на растущем кэше ему нужно
            // проверить, дошла ли сборка до этого кадра (фаза 4).
            if (seekTrack is null) track.Backend.SeekToFrame(local);
            else seekTrack(track, local);
        }

        PositionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Передвинуть playhead, не трогая плееры. Нужно при перетаскивании клипа:
    /// сдвиг меняет мастер-время каждого кадра, но декодировать на каждое движение
    /// мыши незачем — кадры встанут на места по отпусканию кнопки.
    /// </summary>
    public void SetPosition(long frame)
    {
        PositionFrame = Math.Clamp(frame, 0, LastFrame);
        PositionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void StepForward(int frames = 1) => Step(frames);
    public void StepBack(int frames = 1) => Step(-frames);

    private void Step(int frames)
    {
        if (!IsOpen || frames == 0) return;

        // Шаг всегда останавливает воспроизведение: иначе позиция уедет под руками.
        Pause();
        SeekToFrame(PositionFrame + frames);
    }

    public void Play(Action<PlayerTrack, long>? seekTrack = null)
    {
        if (!IsOpen || IsPlaying) return;

        // Стартуем с ровно выставленных позиций: иначе расхождение, набранное
        // паузой, поедет дальше вместе с воспроизведением.
        SeekToFrame(PositionFrame, seekTrack);
        Drift = 0;

        foreach (var track in OpenTracks) track.Backend.Play();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Pause()
    {
        if (!IsPlaying) return;

        foreach (var track in OpenTracks) track.Backend.Pause();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void TogglePlayPause(Action<PlayerTrack, long>? seekTrack = null)
    {
        if (IsPlaying) Pause();
        else Play(seekTrack);
    }

    /// <summary>
    /// Периодическая сверка ведомого с мастером: два независимых декодера расходятся
    /// (риск PLAN.md §6). Подтягиваем переходом без остановки — пауза с последующим
    /// стартом дала бы рывок заметнее самого расхождения.
    /// </summary>
    private void CorrectDrift()
    {
        Drift = 0;

        if (!IsPlaying || Master is not { } master || Slave is not { IsOpen: true } slave) return;
        if (slave.Fps <= 0) return;

        var expected = master.Backend.Position + master.Offset - slave.Offset;
        var actual = slave.Backend.Position;
        var deltaMs = (actual - expected).TotalMilliseconds;

        Drift = deltaMs;

        var frameMs = 1000 / slave.Fps;
        if (Math.Abs(deltaMs) < frameMs * DriftFramesThreshold) return;

        var target = TimeSpan.FromTicks(Math.Clamp(expected.Ticks, 0, slave.TimeOf(slave.LastFrame).Ticks));
        slave.Backend.NudgeTo(target);

        Corrected?.Invoke(this, deltaMs);
    }

    /// <summary>Ведомого подтянули: расхождение в миллисекундах со знаком.</summary>
    public event EventHandler<double>? Corrected;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _driftTimer.Stop();

        foreach (var track in Tracks)
        {
            track.Backend.PositionChanged -= OnTrackPosition;
            track.Backend.StateChanged -= OnTrackState;
        }
    }
}
