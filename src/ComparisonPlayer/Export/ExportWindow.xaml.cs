using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using ComparisonPlayer.Chrome;
using ComparisonPlayer.Localization;
using ComparisonPlayer.Tracks;
using Microsoft.Win32;

namespace ComparisonPlayer.Export;

/// <summary>
/// Окно «Вырезать отрезок» (задача #40): что режем, чем режем, куда кладём — и всё
/// это с прогрессом в одном окне, по образцу окна установки FFmpeg.
/// </summary>
/// <remarks>
/// Границы берём из отрезка трека и переводим в время: ffmpeg работает со временем,
/// а не с номерами кадров, и это же снимает разницу между исходником и прокси кэша,
/// у которых номера кадров могут не совпадать.
/// </remarks>
public partial class ExportWindow : AppWindow
{
    private readonly string _sourcePath;
    private readonly TimeSpan _start;
    private readonly TimeSpan _end;
    private readonly long _frames;
    private readonly bool _sourceIsVfr;

    private CancellationTokenSource? _cancel;
    private bool _done;

    /// <summary>Место выбрали руками: пересобирать имя при смене режима больше нельзя.</summary>
    private bool _pathChosen;

    public ExportWindow(PlayerTrack track)
    {
        InitializeComponent();

        var media = track.Media!;
        _sourcePath = media.FilePath;

        // Частота исходника: в режиме кэша трек считает кадры по прокси, а вырезаем
        // мы оригинал — число кадров отрезка в нём своё.
        var sourceFps = media.FromCache && track.CacheEntry is { SourceFps: > 0 } entry
            ? entry.SourceFps
            : track.Fps;

        _start = track.TimeOf(track.InFrame);
        _end = track.TimeOf(track.OutFrame + 1);
        _frames = sourceFps > 0 ? (long)Math.Round((_end - _start).TotalSeconds * sourceFps) : 0;
        _sourceIsVfr = media.IsVariableFrameRate;

        TxtTrack.Text = $"{track.Letter} · {media.FileName}";
        TxtRange.Text = $"{Timecode(_start)} → {Timecode(_end)}";
        TxtLength.Text = _frames > 0
            ? Loc.Str("Export.LengthValue", _frames, $"{(_end - _start).TotalSeconds:0.00}", $"{sourceFps:0.###}")
            : Loc.Str("Export.LengthShort", $"{(_end - _start).TotalSeconds:0.00}");
        TxtSource.Text = $"{media.Width}×{media.Height} · {media.Codec}"
                         + (media.IsVariableFrameRate ? Loc.Str("Export.SourceVfr") : "");

        TxtPath.Text = SegmentExporter.SuggestPath(_sourcePath, _start, _end, Mode);
        ShowModeHint();
        TxtStatus.Text = Loc.Str("Export.FromSource");
    }

    /// <summary>
    /// Вырезанный файл, который просили открыть в плеере вместо текущего ролика;
    /// <c>null</c> — окно закрыли, ничего не открывая. Забирает его окно-владелец:
    /// треками и раскладкой распоряжается оно, а не диалог.
    /// </summary>
    public string? FileToOpen { get; private set; }

    private bool Busy => _cancel is not null;

    private ExportMode Mode => ChipCopy.IsChecked == true ? ExportMode.Copy : ExportMode.Precise;

    private string OutputPath
    {
        get => TxtPath.Text;
        set => TxtPath.Text = value;
    }

    // ---------- настройка ----------

    /// <summary>
    /// Смена режима меняет и контейнер по умолчанию (копия потока ложится не во всякий),
    /// поэтому имя пересобираем — но только пока пользователь не выбрал своё сам.
    /// </summary>
    private void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;

        ShowModeHint();
        if (!_pathChosen) OutputPath = SegmentExporter.SuggestPath(_sourcePath, _start, _end, Mode);
    }

    private void ShowModeHint() =>
        TxtModeHint.Text = Loc.Str(Mode == ExportMode.Precise ? "Export.HintPrecise" : "Export.HintCopy");

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var extension = Path.GetExtension(OutputPath);
        var dlg = new SaveFileDialog
        {
            Title = Loc.Str("Export.SaveDialog"),
            FileName = Path.GetFileName(OutputPath),
            InitialDirectory = Path.GetDirectoryName(OutputPath),
            DefaultExt = extension,
            Filter = Loc.Str("Export.Filter", extension),
            OverwritePrompt = true
        };

        if (dlg.ShowDialog(this) != true) return;

        OutputPath = dlg.FileName;
        _pathChosen = true;
    }

    // ---------- работа ----------

    private async void Primary_Click(object sender, RoutedEventArgs e)
    {
        if (Busy) return;

        // После удачного вырезания та же кнопка открывает готовый кусок в плеере —
        // чаще всего его и хотят посмотреть сразу.
        if (_done)
        {
            FileToOpen = OutputPath;
            Close();
            return;
        }

        if (!StartWriting()) return;

        var request = new ExportRequest(
            _sourcePath, OutputPath, _start, _end - _start, _frames, Mode, _sourceIsVfr);

        _cancel = new CancellationTokenSource();

        try
        {
            // Отчёты приходят из потока чтения вывода ffmpeg — в поток окна их
            // переводим сами: Progress<T> здесь не подошёл бы, его контекст задаётся
            // местом создания, а создаётся он в обработчике кнопки.
            await SegmentExporter.RunAsync(
                request,
                p => Dispatcher.BeginInvoke(() => ShowProgress(p)),
                _cancel.Token);

            FinishDone(request.OutputPath);
        }
        catch (OperationCanceledException)
        {
            FinishFailed(Loc.Str("Export.Cancelled"));
        }
        catch (Exception ex)
        {
            FinishFailed(ex.Message);
        }
        finally
        {
            _cancel?.Dispose();
            _cancel = null;
        }
    }

    /// <summary>
    /// Перевести окно в рабочее состояние. Отказывает, если писать некуда: своя папка
    /// исходника может оказаться недоступной для записи (сетевой диск, права).
    /// </summary>
    private bool StartWriting()
    {
        var dir = Path.GetDirectoryName(OutputPath);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            ShowError(Loc.Str("Export.NoFolder"));
            return false;
        }

        PanelFacts.Visibility = Visibility.Collapsed;
        PanelMode.Visibility = Visibility.Collapsed;
        BtnBrowse.IsEnabled = false;
        BtnPrimary.IsEnabled = false;

        TxtHead.Text = Loc.Str("Export.Working");
        TxtStatus.Foreground = (Brush)FindResource("MutedBrush");
        TxtStatus.Text = Loc.Str("Export.Preparing");
        Bar.Visibility = Visibility.Visible;
        Bar.Value = 0;
        return true;
    }

    private void ShowProgress(FFmpegProgress p)
    {
        Bar.Value = p.Percent;

        var eta = p.Eta > TimeSpan.Zero ? Loc.Str("Export.Eta", Duration(p.Eta)) : "";
        var speed = p.Speed > 0 ? Loc.Str("Export.Speed", $"{p.Speed:0.0}") : "";

        TxtStatus.Text = p.Total > 0
            ? Loc.Str("Export.Progress", p.Frame, p.Total, $"{p.Percent:0}", eta, speed)
            : Loc.Str("Export.ProgressShort", p.Frame, eta, speed);
    }

    private void FinishDone(string path)
    {
        _done = true;
        Bar.Value = 100;

        TxtHead.Text = Loc.Str("Export.Done", Size(path));
        TxtHead.Foreground = (Brush)FindResource("OkBrush");
        TxtStatus.Text = path;

        BtnPrimary.IsEnabled = true;
        BtnPrimary.Content = Loc.Str("Export.OpenInPlayer");
        BtnReveal.Visibility = Visibility.Visible;
        BtnSecondary.Content = Loc.Str("Export.Close");
    }

    private void Reveal_Click(object sender, RoutedEventArgs e) => Reveal(OutputPath);

    /// <summary>
    /// Вернуть окно к настройке: отказ ffmpeg чаще всего лечится сменой режима или
    /// места записи, и закрывать ради этого окно незачем.
    /// </summary>
    private void FinishFailed(string message)
    {
        Bar.Visibility = Visibility.Collapsed;
        PanelFacts.Visibility = Visibility.Visible;
        PanelMode.Visibility = Visibility.Visible;
        BtnBrowse.IsEnabled = true;
        BtnPrimary.IsEnabled = true;

        TxtHead.Text = Loc.Str("Export.Head");
        ShowError(message);
    }

    private void ShowError(string message)
    {
        TxtStatus.Foreground = (Brush)FindResource("WarnBrush");
        TxtStatus.Text = message;
    }

    private void Secondary_Click(object sender, RoutedEventArgs e)
    {
        // Пока идёт работа, эта кнопка — отмена вырезания, а не закрытие окна.
        if (Busy)
        {
            _cancel?.Cancel();
            return;
        }

        Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        _cancel?.Cancel();
    }

    // ---------- мелочи ----------

    /// <summary>Открыть проводник на готовом файле; пропал файл — открываем папку.</summary>
    private static void Reveal(string path)
    {
        try
        {
            var argument = File.Exists(path)
                ? $"/select,\"{path}\""
                : $"\"{Path.GetDirectoryName(path)}\"";

            Process.Start(new ProcessStartInfo("explorer.exe", argument) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // Проводник — удобство, а не часть работы: молча переживаем отказ.
        }
    }

    private static string Timecode(TimeSpan t) => t.ToString(@"hh\:mm\:ss\.fff");

    private static string Duration(TimeSpan t) =>
        t.TotalMinutes >= 1
            ? Loc.Str("Export.Minutes", (int)t.TotalMinutes, t.Seconds)
            : Loc.Str("Export.Seconds", Math.Max(t.Seconds, 1));

    private static string Size(string path)
    {
        try
        {
            var bytes = new FileInfo(path).Length;
            return bytes >= 1024L * 1024 * 1024
                ? Loc.Str("Units.Gb", $"{bytes / (1024.0 * 1024 * 1024):0.0}")
                : Loc.Str("Units.Mb", $"{bytes / (1024.0 * 1024):0.0}");
        }
        catch (Exception)
        {
            return Loc.Str("Export.SizeUnknown");
        }
    }
}
