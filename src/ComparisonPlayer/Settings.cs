using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ComparisonPlayer;

/// <summary>Режим дискового кэша кадров (PLAN.md §4.1: авторешение плюс ручное переключение).</summary>
public enum FrameCacheMode
{
    /// <summary>Строить, если замеренный шаг назад медленнее порога.</summary>
    Auto,

    /// <summary>Строить для любого открытого ролика, без замера.</summary>
    Always,

    /// <summary>Не строить и не использовать: только прямой декод.</summary>
    Never
}

/// <summary>
/// Настройки, переживающие перезапуск. Пока их немного, поэтому это простой
/// JSON-файл в профиле пользователя, без отдельного слоя конфигурации.
/// </summary>
public sealed class Settings
{
    /// <summary>Показывать таймкод и номер кадра поверх изображения (клавиша T).</summary>
    public bool ShowOverlay { get; set; } = true;

    /// <summary>Каталог последнего открытого файла — с него начинается диалог открытия.</summary>
    public string? LastFolder { get; set; }

    /// <summary>
    /// Повторять отрезок при воспроизведении (клавиша L). По умолчанию выключено:
    /// плеер сначала ведёт себя как плеер, петля включается, когда она нужна.
    /// </summary>
    public bool LoopSegment { get; set; }

    /// <summary>Притягивать playhead и ручки отрезка к границам кадров и ролика (клавиша S).</summary>
    public bool SnapToFrames { get; set; } = true;

    /// <summary>Когда строить дисковый кэш кадров: авто по замеру, всегда, никогда.</summary>
    public FrameCacheMode CacheMode { get; set; } = FrameCacheMode.Auto;

    /// <summary>
    /// Порог автоматического решения: шаг назад медленнее этого — строим кэш.
    /// 250 мс — граница, за которой покадровая работа перестаёт ощущаться отзывчивой.
    /// </summary>
    public int StepBackThresholdMs { get; set; } = 250;

    /// <summary>
    /// Частота кадров прокси. 0 — как в исходнике (покадровое соответствие один к одному).
    /// Меньшая частота уменьшает кэш и время сборки ценой пропущенных кадров, поэтому
    /// входит в ключ кэша: прокси на 15 fps и на 30 fps — разные записи.
    /// </summary>
    public double CacheFps { get; set; }

    /// <summary>Предел дискового кэша; сверх него вытесняются давно не открывавшиеся ролики.</summary>
    public double CacheLimitGb { get; set; } = 20;

    // ---------- фаза 5 ----------

    /// <summary>
    /// Восстанавливать при запуске последнюю сессию (файлы, сдвиг, отрезки, позицию).
    /// Файлы из командной строки сильнее: их открыли осознанно именно сейчас.
    /// </summary>
    public bool RestoreSession { get; set; } = true;

    /// <summary>Шаг стрелки с Shift и кнопок «крупный шаг», в кадрах мастера.</summary>
    public int BigStepFrames { get; set; } = 10;

    /// <summary>Крупный шаг быстрой прокрутки колесом, в кадрах мастера.</summary>
    public int WheelFastFrames { get; set; } = 10;

    /// <summary>
    /// Промежуток между щелчками колеса, ниже которого прокрутка считается быстрой
    /// и идёт крупным шагом. Медленная прокрутка всегда покадровая.
    /// </summary>
    public int WheelFastMs { get; set; } = 70;

    /// <summary>Обратить направление прокрутки колесом над кадром.</summary>
    public bool WheelInverted { get; set; }

    /// <summary>Предел разгона шаттла J/L: 1× → 2× → 4× → … до этого значения.</summary>
    public double ShuttleMaxSpeed { get; set; } = 8;

    /// <summary>
    /// Копия для окна настроек: оно правит её, а не живые настройки, — отмена должна
    /// отменять. Полей здесь только простые типы, поэтому поверхностной копии довольно.
    /// </summary>
    public Settings Clone() => (Settings)MemberwiseClone();

    // Режим кэша пишется словом, а не числом: файл настроек правят руками.
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static Settings Load()
    {
        try
        {
            // Те же Options, что и при записи: без конвертера enum'ов режим кэша
            // («Auto») не читается, разбор падает и настройки молча сбрасываются.
            if (File.Exists(AppEnv.SettingsFile))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(AppEnv.SettingsFile), Options) ?? new Settings();
        }
        catch (Exception)
        {
            // Битый или недоступный файл настроек не должен мешать запуску — берём значения по умолчанию.
        }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppEnv.DataDir);
            File.WriteAllText(AppEnv.SettingsFile, JsonSerializer.Serialize(this, Options));
        }
        catch (Exception)
        {
            // Настройки — удобство, а не данные пользователя: молча переживаем отказ записи.
        }
    }
}
