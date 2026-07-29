using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using ComparisonPlayer.Chrome;

namespace ComparisonPlayer;

/// <summary>
/// Окно настроек (фаза 5). Правит копию настроек и отдаёт её окну только по «Сохранить»:
/// отмена должна отменять, а часть значений (режим кэша, частота прокси) меняет поведение
/// открытых треков — применять их на каждый щелчок было бы неожиданно.
/// </summary>
public partial class SettingsWindow : AppWindow
{
    /// <summary>Раздел со шпаргалкой по клавишам: на него открывается окно по F1.</summary>
    public const string KeysPage = "keys";

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
        ("M", "сделать активный трек мастером (звук идёт за ним)"),
        ("Ctrl + M", "включить или выключить звук"),
        ("Ctrl + ↑ / ↓", "громкость"),
        ("V", "рядом / только A / только B"),
        ("F11", "во весь экран (выход — Esc); двойной клик по кадру — то же"),
        ("Z", "кадр в области: вписать / заполнить / растянуть"),
        ("T", "таймкод поверх кадра"),
        ("Ctrl + T", "свернуть таймлайн до полосы прогресса"),
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

    /// <param name="page">
    /// Раздел, на котором открыть окно (<see cref="KeysPage"/> и прочие теги навигации);
    /// <c>null</c> — «Общие».
    /// </param>
    public SettingsWindow(Settings current, string? page = null)
    {
        InitializeComponent();

        Result = current.Clone();
        ShowValues(Result);
        FillShortcuts();

        if (page is not null) ShowPage(page);

        ShowAssociations();

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
        ChkAutoPlay.IsChecked = s.AutoPlayOnOpen;
        ChkPauseOnSeek.IsChecked = s.PauseOnSeek;
        ChkWheelInverted.IsChecked = s.WheelInverted;

        (s.StartupLayout switch
        {
            StartupLayoutMode.Side => RbStartSide,
            StartupLayoutMode.OnlyA => RbStartOnlyA,
            StartupLayoutMode.OnlyB => RbStartOnlyB,
            _ => RbStartRemembered
        }).IsChecked = true;

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
        s.AutoPlayOnOpen = ChkAutoPlay.IsChecked == true;
        s.PauseOnSeek = ChkPauseOnSeek.IsChecked == true;
        s.WheelInverted = ChkWheelInverted.IsChecked == true;

        s.StartupLayout = RbStartSide.IsChecked == true ? StartupLayoutMode.Side
            : RbStartOnlyA.IsChecked == true ? StartupLayoutMode.OnlyA
            : RbStartOnlyB.IsChecked == true ? StartupLayoutMode.OnlyB
            : StartupLayoutMode.Remembered;

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

    // ---------- разделы ----------

    /// <summary>Показать раздел по тегу пункта навигации; неизвестный тег ничего не меняет.</summary>
    public void ShowPage(string tag)
    {
        var item = NavItems().FirstOrDefault(x => (string?)x.Tag == tag);
        if (item is not null) item.IsChecked = true;
    }

    private IEnumerable<RadioButton> NavItems() => [NavGeneral, NavStartup, NavStep, NavCache, NavAssoc, NavPaths, NavKeys];

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string tag }) return;
        if (PageGeneral is null) return;   // разметка ещё строится: IsChecked стоит прямо в XAML

        PageGeneral.Visibility = Page(tag == "general");
        PageStartup.Visibility = Page(tag == "startup");
        PageStep.Visibility = Page(tag == "step");
        PageCache.Visibility = Page(tag == "cache");
        PageAssoc.Visibility = Page(tag == "assoc");
        PagePaths.Visibility = Page(tag == "paths");
        PageKeys.Visibility = Page(tag == KeysPage);

        // Свиток свой у каждого раздела только на вид: контейнер один, и без этого
        // новый раздел открывался бы прокрученным на позицию прежнего.
        PageScroll.ScrollToTop();

        static Visibility Page(bool shown) => shown ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------- типы файлов ----------

    /// <summary>
    /// Состояние регистрации и надпись на кнопке. Читаем реестр каждый раз заново: ассоциации
    /// живут в системе, а не в настройках, и могли измениться мимо нас — переустановкой
    /// программы или другим приложением.
    /// </summary>
    private void ShowAssociations()
    {
        var scope = FileAssociations.Scope;

        AssocState.Text = scope switch
        {
            AssociationScope.User => "CVP зарегистрирован для видеофайлов у этого пользователя.",
            AssociationScope.Machine =>
                "CVP зарегистрирован установщиком для всех пользователей компьютера. " +
                "Снять такую регистрацию можно удалением программы.",
            _ => "CVP не зарегистрирован: в меню «Открыть с помощью» его нет."
        };

        // Машинную запись обычным процессом не снять — прав не хватит, и кнопка бы только обманывала.
        BtnAssocRegister.Content = scope == AssociationScope.User ? "Убрать регистрацию" : "Зарегистрировать CVP";
        BtnAssocRegister.IsEnabled = scope != AssociationScope.Machine;

        AssocExtensions.Text = "Расширения: " + string.Join(", ", FileAssociations.VideoExtensions);
    }

    private void AssocRegister_Click(object sender, RoutedEventArgs e)
    {
        var registered = FileAssociations.Scope == AssociationScope.User;

        try
        {
            if (registered) FileAssociations.Unregister();
            else FileAssociations.Register();
        }
        catch (Exception ex)
        {
            MessageDialog.Show(this, "Типы файлов",
                $"Не удалось {(registered ? "снять регистрацию" : "зарегистрировать")} типы файлов.\n\n{ex.Message}");
        }

        ShowAssociations();
    }

    private void AssocDefaults_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            FileAssociations.OpenDefaultAppsSettings();
        }
        catch (Exception ex)
        {
            MessageDialog.Show(this, "Типы файлов",
                $"Не открылось окно «Приложения по умолчанию».\n\n{ex.Message}");
        }
    }

    // ---------- шпаргалка ----------

    /// <summary>
    /// Клавиши одним столбцом: у раздела теперь своя страница на всю высоту окна,
    /// и разбивать список на две колонки, как в общем свитке, больше незачем.
    /// </summary>
    private void FillShortcuts()
    {
        for (var i = 0; i < Shortcuts.Length; i++)
        {
            KeyGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var (keys, what) = Shortcuts[i];

            var key = new TextBlock { Text = keys, Style = (Style)FindResource("KeyCell") };
            Grid.SetRow(key, i);
            Grid.SetColumn(key, 0);
            KeyGrid.Children.Add(key);

            var text = new TextBlock { Text = what, Style = (Style)FindResource("KeyText") };
            Grid.SetRow(text, i);
            Grid.SetColumn(text, 1);
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
            MessageDialog.Show(this, "Настройки", "Каталог FFmpeg не найден.",
                "COMPARISONPLAYER_FFMPEG_DIR либо подкаталог FFmpeg рядом с программой");
            return;
        }

        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageDialog.Show(this, "Настройки", $"Не открылся каталог.\n\n{ex.Message}", path);
        }
    }
}
