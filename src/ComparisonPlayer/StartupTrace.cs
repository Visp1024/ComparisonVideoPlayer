using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace ComparisonPlayer;

/// <summary>
/// Отметки этапов запуска (задача #31). Ускорять холодный старт вслепую нельзя:
/// без разбивки по этапам любое изменение выглядит одинаково убедительно.
///
/// Выключена, пока не задана переменная окружения <c>CVP_STARTUP_TRACE</c>: в обычном
/// запуске это пара сравнений строки на весь старт. Значение — путь к файлу отчёта;
/// <c>1</c> означает файл <c>startup-trace.log</c> в каталоге данных приложения.
/// Отсчёт идёт от создания процесса, поэтому в отчёт попадает и время рантайма
/// до входа в <c>Main</c> — самая крупная часть холодного старта.
/// </summary>
public static class StartupTrace
{
    private static readonly List<(string Stage, double Ms)> Marks = [];
    private static readonly string? Target = ResolveTarget();
    private static DateTime _processStart;

    /// <summary>Включена ли трассировка: вызывающему коду незачем знать про переменные окружения.</summary>
    public static bool Enabled => Target is not null;

    private static string? ResolveTarget()
    {
        var value = Environment.GetEnvironmentVariable("CVP_STARTUP_TRACE");
        if (string.IsNullOrWhiteSpace(value)) return null;

        return value is "1" or "true" ? Path.Combine(AppEnv.DataDir, "startup-trace.log") : value;
    }

    /// <summary>
    /// Отметить пройденный этап. Время процесса читаем один раз: обращение к
    /// <see cref="Process.StartTime"/> само по себе не бесплатно.
    /// </summary>
    public static void Mark(string stage)
    {
        if (Target is null) return;

        if (_processStart == default)
            _processStart = Process.GetCurrentProcess().StartTime.ToUniversalTime();

        Marks.Add((stage, (DateTime.UtcNow - _processStart).TotalMilliseconds));
    }

    /// <summary>
    /// Записать отчёт. Файл дописывается: серия запусков подряд — это и есть замер,
    /// одиночный запуск о разбросе ничего не говорит.
    /// </summary>
    public static void Flush()
    {
        if (Target is null || Marks.Count == 0) return;

        try
        {
            var line = new StringBuilder();
            line.Append(DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture));
            line.Append(" pid=").Append(Environment.ProcessId);

            foreach (var (stage, ms) in Marks)
                line.Append(' ').Append(stage).Append('=').Append(ms.ToString("0.0", CultureInfo.InvariantCulture));

            Directory.CreateDirectory(Path.GetDirectoryName(Target)!);
            File.AppendAllText(Target, line.AppendLine().ToString());
        }
        catch (Exception)
        {
            // Замер — вспомогательный инструмент: его отказ не должен ломать запуск.
        }
    }
}
