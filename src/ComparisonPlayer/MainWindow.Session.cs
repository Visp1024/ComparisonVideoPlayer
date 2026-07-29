using System.IO;
using System.Windows;
using ComparisonPlayer.Tracks;
using Microsoft.Win32;

namespace ComparisonPlayer;

/// <summary>
/// Фаза 5: сессия сравнения. Пара роликов, их выравнивание и отрезки — результат
/// ручной работы, и терять его при закрытии окна нельзя: последняя сессия пишется
/// автоматически и восстанавливается при запуске, а отдельным файлом её можно
/// сохранить и передать.
/// </summary>
public partial class MainWindow
{
    /// <summary>Открытая или сохранённая сессия-файл; null — работаем с автосессией.</summary>
    private string? _sessionPath;

    // ---------- снимок состояния ----------

    /// <summary>Собрать текущее состояние сравнения.</summary>
    private Session CaptureSession() => new()
    {
        A = CaptureTrack(_a),
        B = CaptureTrack(_b),
        Master = _sync.MasterId.ToString(),
        Active = _active.ToString(),
        PositionFrame = _sync.PositionFrame,
        Speed = _speed,
        Loop = _loop,
        Layout = _layout.ToString()
    };

    private static SessionTrack CaptureTrack(PlayerTrack track) => new()
    {
        File = track.IsOpen ? track.Media!.FilePath : null,
        OffsetSeconds = track.Offset.TotalSeconds,
        InFrame = track.InFrame,

        // Полный отрезок пишем признаком: после перезапуска число кадров может
        // оказаться другим (кэш на пониженной частоте), и записанный «конец»
        // обрезал бы ролик.
        OutFrame = track.IsFullSegment ? -1 : track.OutFrame
    };

    // ---------- восстановление ----------

    /// <summary>
    /// Применить сессию: открыть файлы, вернуть сдвиг, отрезки, мастера и позицию.
    /// Пропавшие файлы не отменяют восстановление — о них сообщаем и открываем остальное.
    /// </summary>
    private void ApplySession(Session session, string what)
    {
        StopShuttle();
        _sync.Pause();

        foreach (var track in _sync.Tracks) CloseFile(track);

        var missing = new List<string>();
        var opened = 0;

        foreach (var (track, saved) in new[] { (_a, session.A), (_b, session.B) })
        {
            if (string.IsNullOrEmpty(saved.File)) continue;

            if (!File.Exists(saved.File))
            {
                missing.Add($"{track.Letter}: {Path.GetFileName(saved.File)}");
                ShowOpenError(track, saved.File, "файла больше нет по прежнему пути");
                continue;
            }

            if (OpenFile(track, saved.File, quiet: true)) opened++;
            else missing.Add($"{track.Letter}: {Path.GetFileName(saved.File)}");
        }

        if (opened == 0)
        {
            Status(missing.Count > 0
                ? $"{what} не восстановлена: не открылось ни одного файла ({string.Join(", ", missing)})"
                : $"{what} пуста — открывать нечего");
            return;
        }

        // Сдвиг и отрезки — после открытия: до него у трека нет ни fps, ни числа кадров.
        foreach (var (track, saved) in new[] { (_a, session.A), (_b, session.B) })
        {
            if (!track.IsOpen) continue;

            track.Offset = saved.OffsetSeconds > 0 ? TimeSpan.FromSeconds(saved.OffsetSeconds) : TimeSpan.Zero;
            track.SetIn(Math.Clamp(saved.InFrame, 0, track.LastFrame));
            track.SetOut(saved.OutFrame < 0 ? track.LastFrame : Math.Clamp(saved.OutFrame, 0, track.LastFrame));
        }

        if (Enum.TryParse<TrackId>(session.Master, out var master)) _sync.SetMaster(master);
        if (Enum.TryParse<TrackId>(session.Active, out var active)) SetActiveTrack(active);
        if (Enum.TryParse<LayoutMode>(session.Layout, out var layout)) _layout = layout;

        _loop = session.Loop;
        SetSpeed(session.Speed > 0 ? session.Speed : 1);

        ApplyLayout();
        UpdateState();
        SeekFrame(Math.Clamp(session.PositionFrame, 0, _sync.LastFrame));

        var files = string.Join(" · ", _sync.OpenTracks.Select(t => $"{t.Letter}: {t.Media!.FileName}"));
        Status(missing.Count == 0
            ? $"{what} восстановлена — {files}"
            : $"{what} восстановлена частично — {files}; не найдено: {string.Join(", ", missing)}");
    }

    /// <summary>
    /// Восстановление при запуске. Файлы из командной строки сильнее сессии:
    /// их открыли осознанно именно сейчас.
    /// </summary>
    private void RestoreLastSession()
    {
        if (StartupSession() is not { } session) return;

        ApplySession(session, "прошлая сессия");
    }

    /// <summary>Прочитанная сессия и признак того, что читать её уже пробовали.</summary>
    private Session? _startupSession;
    private bool _startupSessionRead;

    /// <summary>
    /// Прошлая сессия, если её велено восстанавливать и в ней есть что восстанавливать.
    /// Читается один раз: знать её содержимое надо ещё до первой отрисовки окна — по нему
    /// видно, с каким видом кадра оно откроется (задача #37), — а второе чтение того же
    /// файла ничего к этому не добавит.
    /// </summary>
    private Session? StartupSession()
    {
        if (_startupSessionRead) return _startupSession;

        _startupSessionRead = true;
        if (App.Settings.RestoreSession && Session.Load(AppEnv.SessionFile) is { HasFiles: true } session)
            _startupSession = session;

        return _startupSession;
    }

    /// <summary>Записать последнюю сессию при закрытии окна; пустую — стереть.</summary>
    private void SaveLastSession()
    {
        if (!_sync.OpenTracks.Any())
        {
            Session.Delete(AppEnv.SessionFile);
            return;
        }

        CaptureSession().Save(AppEnv.SessionFile);
    }

    // ---------- сессия отдельным файлом ----------

    private void SessionSave_Click(object sender, RoutedEventArgs e) => SaveSessionAs();
    private void SessionOpen_Click(object sender, RoutedEventArgs e) => OpenSessionFile();

    private void SessionRestore_Click(object sender, RoutedEventArgs e)
    {
        if (Session.Load(AppEnv.SessionFile) is not { HasFiles: true } session)
        {
            Status("последняя сессия не сохранена — восстанавливать нечего");
            return;
        }

        ApplySession(session, "последняя сессия");
    }

    private void SaveSessionAs()
    {
        if (!_sync.OpenTracks.Any())
        {
            Status("сохранять нечего: не открыт ни один трек");
            return;
        }

        var dlg = new SaveFileDialog
        {
            Title = "Сохранить сессию",
            Filter = Session.FileFilter,
            DefaultExt = Session.FileExtension,
            FileName = SuggestedSessionName(),
            InitialDirectory = Directory.Exists(App.Settings.LastFolder) ? App.Settings.LastFolder : null
        };

        if (dlg.ShowDialog(this) != true) return;

        var error = CaptureSession().Save(dlg.FileName);
        if (error.Length > 0)
        {
            Status($"сессия не сохранена: {error}");
            return;
        }

        _sessionPath = dlg.FileName;
        Status($"сессия сохранена: {Path.GetFileName(dlg.FileName)}");
        UpdateState();
    }

    private void OpenSessionFile()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Открыть сессию",
            Filter = Session.FileFilter,
            InitialDirectory = Directory.Exists(App.Settings.LastFolder) ? App.Settings.LastFolder : null
        };

        if (dlg.ShowDialog(this) != true) return;

        if (Session.Load(dlg.FileName) is not { } session)
        {
            Status($"не прочитать сессию {Path.GetFileName(dlg.FileName)} — файл повреждён или это не сессия");
            return;
        }

        _sessionPath = dlg.FileName;
        ApplySession(session, $"сессия «{Path.GetFileNameWithoutExtension(dlg.FileName)}»");
        UpdateState();
    }

    /// <summary>Имя по умолчанию: по открытым роликам, чтобы файлы сессий не сливались.</summary>
    private string SuggestedSessionName()
    {
        if (_sessionPath is { } path) return Path.GetFileName(path);

        var names = _sync.OpenTracks.Select(t => Path.GetFileNameWithoutExtension(t.Media!.FileName)).ToList();
        var stem = names.Count > 0 ? string.Join(" - ", names) : "session";

        foreach (var bad in Path.GetInvalidFileNameChars()) stem = stem.Replace(bad, '_');
        if (stem.Length > 80) stem = stem[..80];

        return stem + Session.FileExtension;
    }
}
