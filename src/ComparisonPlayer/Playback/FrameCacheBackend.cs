using System.IO;
using ComparisonPlayer.Cache;

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
    private bool _disposed;

    /// <summary>Запись кэша, из которой идут кадры.</summary>
    public CacheEntry Entry => entry;

    public MediaInfo? Media => _media;
    public bool IsOpen => _media is not null;
    public bool IsPlaying => inner.IsPlaying;
    public TimeSpan Position => inner.Position;
    public long FrameIndex => inner.FrameIndex;

    public event EventHandler? PositionChanged;
    public event EventHandler? StateChanged;

    public OpenResult Open(string path)
    {
        if (!string.Equals(path, entry.SourcePath, StringComparison.OrdinalIgnoreCase))
            return OpenResult.Fail("кэш собран для другого файла");

        if (!File.Exists(entry.ProxyPath))
            return OpenResult.Fail("файл кэша пропал");

        var res = inner.Open(entry.ProxyPath);
        if (!res.Success) return res;

        var proxy = inner.Media!;

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

        inner.PositionChanged += OnPositionChanged;
        inner.StateChanged += OnStateChanged;

        StateChanged?.Invoke(this, EventArgs.Empty);
        PositionChanged?.Invoke(this, EventArgs.Empty);
        return OpenResult.Ok();
    }

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
