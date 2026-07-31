using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using ComparisonPlayer.Chrome;
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
            ? $"{_frames} кадров · {(_end - _start).TotalSeconds:0.00} с · {sourceFps:0.###} fps"
            : $"{(_end - _start).TotalSeconds:0.00} с";
        TxtSource.Text = $"{media.Width}×{media.Height} · {media.Codec}"
                         + (media.IsVariableFrameRate ? " · переменная частота" : "");

        TxtPath.Text = SegmentExporter.SuggestPath(_sourcePath, _start, _end, Mode);
        ShowModeHint();
        TxtStatus.Text = "Вырезаем из исходного файла — прокси кэша для этого не используется.";
    }

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
        TxtModeHint.Text = Mode == ExportMode.Precise
            ? "Перекодирование H.264 CRF 20 со звуком AAC 192k: границы ровно те, что на ручках отрезка."
            : "Копия потока: мгновенно и без потери качества, но начало отъедет к ближайшему ключевому кадру — на длинной группе кадров это секунды.";

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var extension = Path.GetExtension(OutputPath);
        var dlg = new SaveFileDialog
        {
            Title = "Куда сохранить отрезок",
            FileName = Path.GetFileName(OutputPath),
            InitialDirectory = Path.GetDirectoryName(OutputPath),
            DefaultExt = extension,
            Filter = $"Видео (*{extension})|*{extension}|Все файлы (*.*)|*.*",
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

        // После удачного вырезания та же кнопка показывает файл в проводнике.
        if (_done)
        {
            Reveal(OutputPath);
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
            FinishFailed("Вырезание отменено, недописанный файл удалён.");
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
            ShowError("Папка для файла не найдена — выберите другое место кнопкой «Обзор…».");
            return false;
        }

        PanelFacts.Visibility = Visibility.Collapsed;
        PanelMode.Visibility = Visibility.Collapsed;
        BtnBrowse.IsEnabled = false;
        BtnPrimary.IsEnabled = false;

        TxtHead.Text = "Вырезаю…";
        TxtStatus.Foreground = (Brush)FindResource("MutedBrush");
        TxtStatus.Text = "Подготовка…";
        Bar.Visibility = Visibility.Visible;
        Bar.Value = 0;
        return true;
    }

    private void ShowProgress(FFmpegProgress p)
    {
        Bar.Value = p.Percent;

        var eta = p.Eta > TimeSpan.Zero ? $" · осталось ~{Duration(p.Eta)}" : "";
        var speed = p.Speed > 0 ? $" · {p.Speed:0.0}×" : "";

        TxtStatus.Text = p.Total > 0
            ? $"{p.Frame} из {p.Total} кадров · {p.Percent:0} %{eta}{speed}"
            : $"{p.Frame} кадров{eta}{speed}";
    }

    private void FinishDone(string path)
    {
        _done = true;
        Bar.Value = 100;

        TxtHead.Text = $"Готово · {Size(path)}";
        TxtHead.Foreground = (Brush)FindResource("OkBrush");
        TxtStatus.Text = path;

        BtnPrimary.IsEnabled = true;
        BtnPrimary.Content = "Показать в папке";
        BtnSecondary.Content = "Закрыть";
    }

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

        TxtHead.Text = "Вырезать отрезок в отдельный файл";
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
        t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} мин {t.Seconds} с" : $"{Math.Max(t.Seconds, 1)} с";

    private static string Size(string path)
    {
        try
        {
            var bytes = new FileInfo(path).Length;
            return bytes >= 1024L * 1024 * 1024
                ? $"{bytes / (1024.0 * 1024 * 1024):0.0} ГБ"
                : $"{bytes / (1024.0 * 1024):0.0} МБ";
        }
        catch (Exception)
        {
            return "файл записан";
        }
    }
}
