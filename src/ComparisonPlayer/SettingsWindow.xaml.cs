using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace ComparisonPlayer;

/// <summary>
/// Окно настроек (фаза 5). Правит копию настроек и отдаёт её окну только по «Сохранить»:
/// отмена должна отменять, а часть значений (режим кэша, частота прокси) меняет поведение
/// открытых треков — применять их на каждый щелчок было бы неожиданно.
/// </summary>
public partial class SettingsWindow : Window
{
    /// <summary>Шпаргалка по клавишам: она же справка, отдельного окна помощи нет.</summary>
    private static readonly (string Keys, string What)[] Shortcuts =
    [
        ("Space", "воспроизведение / пауза"),
        ("J / K / L", "шаттл: назад · стоп · вперёд (повтор — вдвое быстрее)"),
        ("← / →", "шаг на кадр"),
        ("Shift + ← / →", "крупный шаг"),
        ("колесо над кадром", "медленно — по кадру, быстро — крупным шагом"),
        ("Home / End", "начало / конец отрезка (с Shift — края шкалы)"),
        ("I / O", "начало / конец отрезка на текущем кадре"),
        ("Shift + I", "сбросить отрезок на весь ролик"),
        ("Ctrl + L", "петля по отрезку"),
        ("Alt + ← / →", "сдвиг трека B на кадр"),
        ("Alt + 0", "сбросить сдвиг"),
        ("Tab", "переключить активный трек"),
        ("M", "сделать активный трек мастером"),
        ("V", "рядом / только A / только B"),
        ("T", "таймкод поверх кадра"),
        ("S", "снэп к границам"),
        ("F", "уместить всю шкалу"),
        ("+ / −", "зум таймлайна"),
        ("Ctrl + O", "открыть файл в трек A (с Shift — в B)"),
        ("Ctrl + I", "панель сведений"),
        ("C", "панель кэша кадров"),
        ("Ctrl + S", "сохранить сессию в файл"),
        ("F1", "это окно")
    ];

    /// <summary>Правленая копия; забирать её имеет смысл только при <c>DialogResult == true</c>.</summary>
    public Settings Result { get; }

    public SettingsWindow(Settings current)
    {
        InitializeComponent();

        Result = current.Clone();
        ShowValues(Result);
        FillShortcuts();

        PathData.Text = AppEnv.DataDir;
        PathData.ToolTip = AppEnv.DataDir;
        PathCache.Text = AppEnv.CacheDir;
        PathCache.ToolTip = AppEnv.CacheDir;
        PathFFmpeg.Text = string.IsNullOrEmpty(AppEnv.FFmpegDir) ? "не найден" : AppEnv.FFmpegDir;
        PathFFmpeg.ToolTip = PathFFmpeg.Text;
    }

    // ---------- настройки ↔ элементы ----------

    private void ShowValues(Settings s)
    {
        ChkRestore.IsChecked = s.RestoreSession;
        ChkOverlay.IsChecked = s.ShowOverlay;
        ChkSnap.IsChecked = s.SnapToFrames;
        ChkLoop.IsChecked = s.LoopSegment;
        ChkWheelInverted.IsChecked = s.WheelInverted;

        TxtBigStep.Text = s.BigStepFrames.ToString(CultureInfo.InvariantCulture);
        TxtWheelFrames.Text = s.WheelFastFrames.ToString(CultureInfo.InvariantCulture);
        TxtWheelMs.Text = s.WheelFastMs.ToString(CultureInfo.InvariantCulture);
        TxtThreshold.Text = s.StepBackThresholdMs.ToString(CultureInfo.InvariantCulture);
        TxtCacheLimit.Text = s.CacheLimitGb.ToString("0.##", CultureInfo.InvariantCulture);

        (s.CacheMode switch
        {
            FrameCacheMode.Always => RbCacheAlways,
            FrameCacheMode.Never => RbCacheNever,
            _ => RbCacheAuto
        }).IsChecked = true;

        CheckChip(CacheFpsChips, s.CacheFps);
        CheckChip(ShuttleChips, s.ShuttleMaxSpeed);
    }

    /// <summary>Прочитать элементы в копию настроек. Непонятное значение оставляет прежнее.</summary>
    private void Collect(Settings s)
    {
        s.RestoreSession = ChkRestore.IsChecked == true;
        s.ShowOverlay = ChkOverlay.IsChecked == true;
        s.SnapToFrames = ChkSnap.IsChecked == true;
        s.LoopSegment = ChkLoop.IsChecked == true;
        s.WheelInverted = ChkWheelInverted.IsChecked == true;

        s.BigStepFrames = Int(TxtBigStep, s.BigStepFrames, 2, 1000);
        s.WheelFastFrames = Int(TxtWheelFrames, s.WheelFastFrames, 2, 1000);
        s.WheelFastMs = Int(TxtWheelMs, s.WheelFastMs, 10, 1000);
        s.StepBackThresholdMs = Int(TxtThreshold, s.StepBackThresholdMs, 10, 10000);
        s.CacheLimitGb = Real(TxtCacheLimit, s.CacheLimitGb, 0.5, 2000);

        s.CacheMode = RbCacheAlways.IsChecked == true ? FrameCacheMode.Always
            : RbCacheNever.IsChecked == true ? FrameCacheMode.Never
            : FrameCacheMode.Auto;

        s.CacheFps = ChipValue(CacheFpsChips) ?? s.CacheFps;
        s.ShuttleMaxSpeed = ChipValue(ShuttleChips) ?? s.ShuttleMaxSpeed;
    }

    private static int Int(TextBox box, int fallback, int min, int max) =>
        int.TryParse(box.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? Math.Clamp(value, min, max)
            : fallback;

    private static double Real(TextBox box, double fallback, double min, double max) =>
        double.TryParse(box.Text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? Math.Clamp(value, min, max)
            : fallback;

    private static void CheckChip(Panel chips, double value)
    {
        var buttons = chips.Children.OfType<RadioButton>().ToList();
        var chip = buttons.FirstOrDefault(x => ChipTag(x) is { } tag && Math.Abs(tag - value) < 0.001);
        if (chip is not null) chip.IsChecked = true;
    }

    private static double? ChipValue(Panel chips) =>
        chips.Children.OfType<RadioButton>().FirstOrDefault(x => x.IsChecked == true) is { } chip ? ChipTag(chip) : null;

    private static double? ChipTag(RadioButton button) =>
        button.Tag is string tag && double.TryParse(tag, CultureInfo.InvariantCulture, out var value) ? value : null;

    // ---------- шпаргалка ----------

    /// <summary>Разложить клавиши в две колонки: списком в один столбец окно вытянулось бы вдвое.</summary>
    private void FillShortcuts()
    {
        var rows = (Shortcuts.Length + 1) / 2;
        for (var i = 0; i < rows; i++) KeyGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (var i = 0; i < Shortcuts.Length; i++)
        {
            var (keys, what) = Shortcuts[i];
            var column = i < rows ? 0 : 2;
            var row = i < rows ? i : i - rows;

            var key = new TextBlock { Text = keys, Style = (Style)FindResource("KeyCell") };
            Grid.SetRow(key, row);
            Grid.SetColumn(key, column);
            KeyGrid.Children.Add(key);

            var text = new TextBlock { Text = what, Style = (Style)FindResource("KeyText") };
            Grid.SetRow(text, row);
            Grid.SetColumn(text, column + 1);
            KeyGrid.Children.Add(text);
        }
    }

    // ---------- кнопки ----------

    private void Defaults_Click(object sender, RoutedEventArgs e)
    {
        // Каталог последнего открытия — не настройка, а память интерфейса: его сброс
        // только раздражал бы, поэтому переносим в чистые значения.
        var defaults = new Settings { LastFolder = Result.LastFolder };
        ShowValues(defaults);
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Collect(Result);
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string what }) return;

        var path = what switch
        {
            "cache" => AppEnv.CacheDir,
            "ffmpeg" => AppEnv.FFmpegDir,
            _ => AppEnv.DataDir
        };

        if (string.IsNullOrEmpty(path))
        {
            MessageBox.Show(this, "Каталог FFmpeg не найден.", "Настройки",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Не открылся каталог:\n{path}\n\n{ex.Message}", "Настройки",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
