using System.IO;
using System.Text.Json;

namespace ComparisonPlayer;

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

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(AppEnv.SettingsFile))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(AppEnv.SettingsFile)) ?? new Settings();
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
