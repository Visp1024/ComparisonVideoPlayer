using System.ComponentModel;
using System.IO;
using System.Linq;
using Flyleaf.FFmpeg;
using FlyleafLib;
using FlyleafLib.MediaFramework.MediaStream;
using FlyleafLib.MediaPlayer;

namespace ComparisonPlayer.Playback;

/// <summary>
/// Основной движок: прямой декод через FlyleafLib (FFmpeg + D3D11).
/// Конфигурация унаследована от спайка фазы 0: точный seek обязателен,
/// автозапуск выключен, декод аппаратный. Звук декодируется у обоих треков,
/// но слышен только у мастера — кто именно приглушён, решает SyncEngine.
/// </summary>
public sealed class FlyleafBackend : IPlaybackBackend
{
    private readonly Player _player;
    private MediaInfo? _media;
    private bool _disposed;

    public FlyleafBackend()
    {
        _player = new Player(CreateConfig());
        _player.PropertyChanged += OnPlayerPropertyChanged;
    }

    /// <summary>
    /// Плеер FlyleafLib для привязки к FlyleafHost. Единственное место,
    /// где UI видит конкретную библиотеку — сам интерфейс о ней не знает.
    /// </summary>
    public Player Player => _player;

    public MediaInfo? Media => _media;
    public bool IsOpen => _media is not null;
    public bool IsPlaying => _player.IsPlaying;

    public TimeSpan Position => TimeSpan.FromTicks(Math.Max(_player.CurTime, 0));

    public long FrameIndex => _media?.FrameAt(Position) ?? 0;

    public double Speed
    {
        get => _player.Speed;
        set => _player.Speed = Math.Clamp(value, 0.05, 16);
    }

    /// <summary>
    /// Список дорожек заполняется при открытии файла, поэтому у закрытого движка
    /// он пуст — отдельной проверки <see cref="IsOpen"/> не нужно.
    /// </summary>
    public bool HasAudio => _player.Audio.Streams is { Count: > 0 };

    public int Volume
    {
        get => _player.Audio.Volume;
        set => _player.Audio.Volume = Math.Clamp(value, 0, 100);
    }

    public bool Muted
    {
        get => _player.Audio.Mute;
        set => _player.Audio.Mute = value;
    }

    public event EventHandler? PositionChanged;
    public event EventHandler? StateChanged;

    private static Config CreateConfig()
    {
        var cfg = new Config();
        cfg.Player.AutoPlay = false;
        cfg.Player.SeekAccurate = true;      // покадровая точность seek — требование проекта
        // Звук нужен только с мастер-трека (задача #20), но включён он у обоих:
        // приглушение — свойство плеера, и переключать мастера им дешевле, чем
        // переоткрывать звуковую дорожку на каждое нажатие M.
        cfg.Audio.Enabled = true;
        cfg.Video.VideoAcceleration = true;  // аппаратный декод D3D11

        // Плавный playhead: по умолчанию FlyleafLib отдаёт CurTime раз в секунду,
        // и на таймлайне это выглядело как рывок раз в секунду вместо хода по кадрам.
        // PerFrame — обновление на каждый показанный кадр, ровно то, что нужно
        // покадровому инструменту.
        cfg.Player.UICurTime = UIRefreshType.PerFrame;

        // Свои горячие клавиши FlyleafLib перехватывает раньше окна и помечает
        // событие обработанным — из-за этого до плеера не доходили клавиши
        // приложения. Транспортом управляет только UI, поэтому набор чистим.
        // (список пуст, пока FlyleafLib не загрузил в него набор по умолчанию)
        cfg.Player.KeyBindings.Keys?.Clear();

        return cfg;
    }

    /// <summary>
    /// Как кадр вписан в отведённую ему область (задача #28). Настройка чисто
    /// отрисовочная: ни позиция, ни номер кадра от неё не зависят, поэтому живёт
    /// здесь, а не в <see cref="IPlaybackBackend"/> — кадры из кэша рисует этот же
    /// Player, и режим переживает переключение движков сам собой.
    /// </summary>
    public void ApplyScale(VideoScaleMode mode)
    {
        var video = _player.Config.Video;

        // Растянуть — это и есть «соотношение сторон как у области вывода».
        if (mode == VideoScaleMode.Stretch)
        {
            video.AspectRatio = AspectRatio.Fill;
            video.Zoom = FitZoom;
            return;
        }

        video.AspectRatio = AspectRatio.Keep;
        video.Zoom = mode == VideoScaleMode.Fill ? FitZoom * FillFactor() : FitZoom;
    }

    /// <summary>Zoom у FlyleafLib в процентах: 100 — кадр вписан в область как есть.</summary>
    private const double FitZoom = 100;

    /// <summary>
    /// Во сколько раз увеличить вписанный кадр, чтобы он заполнил область целиком.
    /// Вписанный упирается в одну пару сторон; заполнение — упереться и во вторую,
    /// лишнее уходит за края (Zoom считается от центра, ZoomCenter по умолчанию 0.5).
    /// </summary>
    private double FillFactor()
    {
        var renderer = _player.Renderer;
        if (renderer is null || renderer.ControlWidth <= 0 || renderer.ControlHeight <= 0) return 1;

        // DAR учитывает неквадратный пиксель — брать Width/Height файла было бы неверно
        // для анаморфного материала; на закрытом файле его ещё нет, там режим не важен.
        var frame = renderer.DAR.Value;
        if (frame <= 0) return 1;

        var area = renderer.ControlWidth / (double)renderer.ControlHeight;
        return Math.Max(area / frame, frame / area);
    }

    public OpenResult Open(string path)
    {
        if (!File.Exists(path))
            return OpenResult.Fail($"файл не найден: {path}");

        var res = _player.Open(path);
        if (!res.Success)
            return OpenResult.Fail(string.IsNullOrWhiteSpace(res.Error) ? "формат не поддерживается" : res.Error);

        var stream = SelectedVideoStream();
        if (stream is null)
        {
            _player.Stop();
            return OpenResult.Fail("в файле нет видеодорожки");
        }

        _media = Describe(path, stream);

        // Автозапуска нет, а пустой чёрный прямоугольник вместо кадра выглядит
        // как несработавшее открытие — показываем первый кадр сразу.
        _player.ShowFrame(0);

        StateChanged?.Invoke(this, EventArgs.Empty);
        PositionChanged?.Invoke(this, EventArgs.Empty);
        return OpenResult.Ok();
    }

    public void Close()
    {
        if (!IsOpen) return;
        _player.Stop();
        _media = null;
        StateChanged?.Invoke(this, EventArgs.Empty);
        PositionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Play()
    {
        if (!IsOpen || _player.IsPlaying) return;
        _player.Play();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Pause()
    {
        if (!IsOpen || !_player.IsPlaying) return;
        _player.Pause();
        StateChanged?.Invoke(this, EventArgs.Empty);
        PositionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void TogglePlayPause()
    {
        if (_player.IsPlaying) Pause(); else Play();
    }

    public void StepForward(int frames = 1) => Step(frames, forward: true);
    public void StepBack(int frames = 1) => Step(frames, forward: false);

    private void Step(int frames, bool forward)
    {
        if (!IsOpen || frames <= 0) return;

        // Шаг всегда останавливает воспроизведение: иначе позиция уедет прямо под руками.
        if (_player.IsPlaying)
        {
            _player.Pause();
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        for (var i = 0; i < frames; i++)
        {
            if (forward) _player.ShowFrameNext();
            else _player.ShowFramePrev();
        }

        PositionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SeekToFrame(long frame)
    {
        if (_media is null) return;

        var target = (int)Math.Clamp(frame, 0, Math.Max(_media.FrameCount - 1, 0));
        if (_player.IsPlaying)
        {
            _player.Pause();
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        _player.ShowFrame(target);
        PositionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SeekTo(TimeSpan position)
    {
        if (_media is null) return;

        // При известной частоте идём через номер кадра: так позиция ложится
        // ровно на границу кадра, а не между двумя соседними.
        if (_media.Fps > 0 && !_media.IsVariableFrameRate)
        {
            SeekToFrame(_media.FrameAt(position));
            return;
        }

        var ms = (int)Math.Clamp(position.TotalMilliseconds, 0, _media.Duration.TotalMilliseconds);
        _player.SeekAccurate(ms);
        PositionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Коррекция дрейфа: точный переход, не трогающий состояние воспроизведения.
    /// В отличие от <see cref="SeekToFrame"/> паузу не ставит — ведомый трек
    /// подтягивается на ходу.
    /// </summary>
    public void NudgeTo(TimeSpan position)
    {
        if (_media is null) return;

        var ms = (int)Math.Clamp(position.TotalMilliseconds, 0, _media.Duration.TotalMilliseconds);
        _player.SeekAccurate(ms);
    }

    private VideoStream? SelectedVideoStream()
    {
        var streams = _player.Video.Streams;
        if (streams is null || streams.Count == 0) return null;
        return streams.FirstOrDefault(s => s.StreamIndex == _player.Video.StreamIndex) ?? streams[0];
    }

    private MediaInfo Describe(string path, VideoStream stream)
    {
        var fps = stream.FPS;
        var duration = TimeSpan.FromTicks(Math.Max(_player.Duration, 0));

        var frames = stream.TotalFrames > 0
            ? stream.TotalFrames
            : (long)Math.Round(duration.TotalSeconds * fps);

        return new MediaInfo(
            FilePath: path,
            Codec: string.IsNullOrWhiteSpace(stream.Codec) ? "—" : stream.Codec,
            Width: (int)stream.Width,
            Height: (int)stream.Height,
            Fps: fps,
            Duration: duration,
            FrameCount: Math.Max(frames, 0),
            IsVariableFrameRate: DetectVariableFrameRate(stream),
            HardwareAcceleration: _player.Video.VideoAcceleration);
    }

    /// <summary>
    /// VFR определяем так же, как это делают по выводу ffprobe: у ролика с постоянной
    /// частотой r_frame_rate (максимальная) совпадает с avg_frame_rate (средняя),
    /// у VFR — заметно выше. Точного флага в контейнере нет, это единственный
    /// доступный признак до полного прохода по пакетам.
    /// </summary>
    private static unsafe bool DetectVariableFrameRate(VideoStream stream)
    {
        var av = stream.AVStream;
        if (av == null) return false;

        var avg = Rate(av->avg_frame_rate);
        var real = Rate(av->r_frame_rate);
        if (avg <= 0 || real <= 0) return false;

        return Math.Abs(real - avg) > 0.01 * Math.Max(real, avg);

        static double Rate(AVRational r) => r.Den != 0 ? (double)r.Num / r.Den : 0;
    }

    private void OnPlayerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(Player.CurTime):
                PositionChanged?.Invoke(this, EventArgs.Empty);
                break;
            case nameof(Player.IsPlaying):
            case nameof(Player.Status):
                StateChanged?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _player.PropertyChanged -= OnPlayerPropertyChanged;
        _player.Dispose();
    }
}
