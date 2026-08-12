using System.IO;
using ComparisonPlayer.Cache;
using ComparisonPlayer.Localization;

namespace ComparisonPlayer.Playback;

/// <summary>
/// Движок «кадры из кэша»: играет собранный ffmpeg'ом all-intra прокси вместо
/// исходного ролика, но снаружи выглядит как открытый исходник — путь, кодек и
/// разрешение в <see cref="MediaInfo"/> остаются от него.
///
/// Реализован обёрткой над тем же <see cref="FlyleafBackend"/>, а не отдельным
/// декодером: прокси — обычный видеофайл, и весь выигрыш даёт его структура
/// (каждый кадр ключевой). Общий Player заодно избавляет от перепривязки
/// FlyleafHost при переключении режимов — картинка не мигает.
/// </summary>
public sealed class FrameCacheBackend(FlyleafBackend inner, CacheEntry entry) : IPlaybackBackend
{
    private MediaInfo? _media;
    private long _available;
    private bool _disposed;

    /// <summary>Запись кэша, из которой идут кадры.</summary>
    public CacheEntry Entry => entry;

    /// <summary>
    /// Сколько кадров реально лежит в прокси на момент открытия. У готовой записи
    /// это все кадры ролика, у собираемой — только уже записанная часть.
    /// </summary>
    public long AvailableFrames => _available;

    public MediaInfo? Media => _media;
    public bool IsOpen => _media is not null;
    public bool IsPlaying => inner.IsPlaying;
    public TimeSpan Position => inner.Position;
    public long FrameIndex => inner.FrameIndex;

    public double Speed
    {
        get => inner.Speed;
        set => inner.Speed = value;
    }

    /// <summary>
    /// Звук берётся из прокси, а не из исходника: играет-то прокси. У записей,
    /// собранных до появления звука в прокси, дорожки нет — панель кэша предлагает
    /// собрать такую запись заново.
    /// </summary>
    public bool HasAudio => inner.HasAudio;

    public int Volume
    {
        get => inner.Volume;
        set => inner.Volume = value;
    }

    public bool Muted
    {
        get => inner.Muted;
        set => inner.Muted = value;
    }

    public event EventHandler? PositionChanged;
    public event EventHandler? StateChanged;

    public OpenResult Open(string path)
    {
        if (!string.Equals(path, entry.SourcePath, StringComparison.OrdinalIgnoreCase))
            return OpenResult.Fail(Loc.Str("Cache.NotForFile"));

        if (!File.Exists(entry.ProxyPath))
            return OpenResult.Fail(Loc.Str("Cache.FileGone"));

        var res = inner.Open(entry.ProxyPath);
        if (!res.Success) return res;

        var proxy = inner.Media!;
        _available = proxy.FrameCount;

        // Частоту, длительность и число кадров берём у прокси: он приведён к
        // постоянной частоте и может отличаться от исходника на кадр — транспорт
        // должен считать по тому, что реально декодируется.
        _media = proxy with
        {
            FilePath = entry.SourcePath,
            Codec = entry.Codec,
            Width = entry.Width > 0 ? entry.Width : proxy.Width,
            Height = entry.Height > 0 ? entry.Height : proxy.Height,
            IsVariableFrameRate = false,
            FromCache = true
        };

        // У собираемого прокси в файле пока меньше кадров, чем в ролике. Шкала и
        // счётчик кадров обязаны показывать весь ролик, иначе они прыгали бы при
        // каждом продлении кэша — поэтому длительность берём из записи.
        if (entry.Partial && entry.FrameCount > _available)
        {
            _media = _media with
            {
                Fps = entry.Fps > 0 ? entry.Fps : _media.Fps,
                FrameCount = entry.FrameCount,
                Duration = entry.Duration
            };
        }

        // Открыть могут и повторно — растущий прокси перечитывают, чтобы увидеть
        // дописанные кадры; двойной подписки на события при этом быть не должно.
        Detach();
        inner.PositionChanged += OnPositionChanged;
        inner.StateChanged += OnStateChanged;

        StateChanged?.Invoke(this, EventArgs.Empty);
        PositionChanged?.Invoke(this, EventArgs.Empty);
        return OpenResult.Ok();
    }

    /// <summary>
    /// Перечитать растущий прокси: пока идёт сборка, дописанные кадры появляются
    /// в файле, но открытый демуксер о них не знает. Позицию восстанавливает вызывающий.
    /// </summary>
    public OpenResult Reopen() => Open(entry.SourcePath);

    public void Close()
    {
        if (!IsOpen) return;

        Detach();
        inner.Close();
        _media = null;

        StateChanged?.Invoke(this, EventArgs.Empty);
        PositionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Play() => inner.Play();
    public void Pause() => inner.Pause();
    public void TogglePlayPause() => inner.TogglePlayPause();
    public void StepForward(int frames = 1) => inner.StepForward(frames);
    public void StepBack(int frames = 1) => inner.StepBack(frames);
    public void SeekToFrame(long frame) => inner.SeekToFrame(frame);
    public void SeekTo(TimeSpan position) => inner.SeekTo(position);
    public void NudgeTo(TimeSpan position) => inner.NudgeTo(position);

    private void OnPositionChanged(object? sender, EventArgs e) => PositionChanged?.Invoke(this, EventArgs.Empty);
    private void OnStateChanged(object? sender, EventArgs e) => StateChanged?.Invoke(this, EventArgs.Empty);

    private void Detach()
    {
        inner.PositionChanged -= OnPositionChanged;
        inner.StateChanged -= OnStateChanged;
    }

    /// <summary>
    /// Отпускает только свою подписку: сам <see cref="FlyleafBackend"/> живёт дольше
    /// обёртки — на него переключаются обратно кнопкой «Играть с исходника».
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Detach();
    }
}
