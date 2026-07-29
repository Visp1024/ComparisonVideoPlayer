using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ComparisonPlayer;

/// <summary>Где лежит действующая регистрация плеера для видеофайлов.</summary>
public enum AssociationScope
{
    /// <summary>Плеер не зарегистрирован — в «Открыть с помощью» его нет.</summary>
    None,

    /// <summary>Запись текущего пользователя (HKCU): её ставит и снимает само приложение.</summary>
    User,

    /// <summary>Запись для всех пользователей (HKLM): её ставит инсталлятор, запущенный с правами администратора.</summary>
    Machine
}

/// <summary>
/// Регистрация плеера как приложения для видеофайлов: ProgID «CVP.Video», список
/// расширений в «Открыть с помощью» и набор возможностей для окна «Приложения по
/// умолчанию».
///
/// Приложением по умолчанию программа себя назначить не может: с Windows 10 выбор
/// пользователя (<c>UserChoice</c>) подписан хэшем, и запись мимо системного диалога
/// Windows отбрасывает. Поэтому здесь только регистрация — а сам выбор делает человек в
/// системном окне, которое открывает <see cref="OpenDefaultAppsSettings"/>.
/// </summary>
public static class FileAssociations
{
    /// <summary>Идентификатор типа файла. Меняться не должен: по нему система помнит выбор пользователя.</summary>
    public const string ProgId = "CVP.Video";

    /// <summary>Расширения, которые плеер открывает: тот же список для диалога, перетаскивания и ассоциаций.</summary>
    public static readonly string[] VideoExtensions =
        [".mp4", ".mkv", ".mov", ".avi", ".ts", ".m4v", ".webm", ".wmv", ".mpg", ".mpeg"];

    /// <summary>Имя приложения в <c>RegisteredApplications</c> — под ним CVP виден в «Приложениях по умолчанию».</summary>
    private const string AppName = "CVP";

    private const string ClassesKey = @"Software\Classes";
    private const string CapabilitiesKey = @"Software\CVP\Capabilities";
    private const string RegisteredAppsKey = @"Software\RegisteredApplications";

    /// <summary>Путь к exe плеера. У single-file поставки он берётся только так: каталог сборки — временный.</summary>
    private static string ExePath => Environment.ProcessPath ?? "";

    /// <summary>
    /// Действующая регистрация. Смотрим не «свою» ветку, а <c>HKEY_CLASSES_ROOT</c> — слияние
    /// пользовательской и машинной: в ней видно и то, что поставил инсталлятор от администратора.
    /// Чужая запись (команда ведёт к другому exe — например, к прежней установке) считается
    /// отсутствующей: в «Открыть с помощью» она приведёт не сюда.
    /// </summary>
    public static AssociationScope Scope
    {
        get
        {
            if (!CommandMatches(Registry.ClassesRoot, ProgId)) return AssociationScope.None;

            return CommandMatches(Registry.CurrentUser, $@"{ClassesKey}\{ProgId}")
                ? AssociationScope.User
                : AssociationScope.Machine;
        }
    }

    /// <summary>Ведёт ли <c>shell\open\command</c> под этим корнем к нашему exe.</summary>
    private static bool CommandMatches(RegistryKey root, string progIdPath)
    {
        try
        {
            using var key = root.OpenSubKey($@"{progIdPath}\shell\open\command");
            if (key?.GetValue(null) is not string command || ExePath.Length == 0) return false;

            // В команде путь закавычен и за ним идёт «%1» — сравниваем по вхождению самого пути.
            return command.Contains(ExePath, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Прописать регистрацию для текущего пользователя. Прав администратора не требует —
    /// поэтому и работает в поставке, установленной в профиль.
    /// </summary>
    public static void Register()
    {
        var exe = ExePath;
        if (exe.Length == 0) throw new InvalidOperationException("не удалось определить путь к программе");

        using (var progId = Registry.CurrentUser.CreateSubKey($@"{ClassesKey}\{ProgId}"))
        {
            progId.SetValue(null, "Видео CVP");
            progId.SetValue("FriendlyTypeName", "Видео CVP");

            using (var icon = progId.CreateSubKey("DefaultIcon"))
                icon.SetValue(null, $"\"{exe}\",0");

            using var command = progId.CreateSubKey(@"shell\open\command");
            command.SetValue(null, $"\"{exe}\" \"%1\"");
        }

        foreach (var ext in VideoExtensions)
        {
            // Именно OpenWithProgIds, а не значение по умолчанию у расширения: так плеер
            // добавляется в «Открыть с помощью», не отбирая у пользователя текущий выбор.
            using var openWith = Registry.CurrentUser.CreateSubKey($@"{ClassesKey}\{ext}\OpenWithProgIds");
            openWith.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
        }

        using (var capabilities = Registry.CurrentUser.CreateSubKey(CapabilitiesKey))
        {
            capabilities.SetValue("ApplicationName", AppName);
            capabilities.SetValue("ApplicationDescription", "Покадровое сравнение двух видео");
            capabilities.SetValue("ApplicationIcon", $"\"{exe}\",0");

            using var associations = capabilities.CreateSubKey("FileAssociations");
            foreach (var ext in VideoExtensions) associations.SetValue(ext, ProgId);
        }

        // Без этой записи «Приложения по умолчанию» приложение не покажут: список окна строится
        // по RegisteredApplications, а не по ProgID.
        using (var registered = Registry.CurrentUser.CreateSubKey(RegisteredAppsKey))
            registered.SetValue(AppName, CapabilitiesKey);

        NotifyShell();
    }

    /// <summary>
    /// Снять регистрацию текущего пользователя. Машинную запись инсталлятора не трогает —
    /// у обычного процесса нет на неё прав, её снимает удаление программы.
    /// </summary>
    public static void Unregister()
    {
        Registry.CurrentUser.DeleteSubKeyTree($@"{ClassesKey}\{ProgId}", throwOnMissingSubKey: false);

        // Software\CVP целиком: кроме возможностей приложение там ничего не держит —
        // настройки и сессия лежат файлами в профиле (AppEnv.DataDir).
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\CVP", throwOnMissingSubKey: false);

        foreach (var ext in VideoExtensions)
        {
            using var openWith = Registry.CurrentUser.OpenSubKey($@"{ClassesKey}\{ext}\OpenWithProgIds", writable: true);
            openWith?.DeleteValue(ProgId, throwOnMissingValue: false);
        }

        using (var registered = Registry.CurrentUser.OpenSubKey(RegisteredAppsKey, writable: true))
            registered?.DeleteValue(AppName, throwOnMissingValue: false);

        NotifyShell();
    }

    /// <summary>
    /// Открыть системное окно «Приложения по умолчанию» на карточке CVP. Параметр
    /// <c>registeredAppName</c> понимает Windows 10 и 11; если приложение системе неизвестно,
    /// откроется общий список — не ошибка, а просто менее удобный путь.
    /// </summary>
    public static void OpenDefaultAppsSettings()
    {
        Process.Start(new ProcessStartInfo($"ms-settings:defaultapps?registeredAppName={AppName}")
        {
            UseShellExecute = true
        });
    }

    /// <summary>
    /// Сказать оболочке, что ассоциации изменились. Без этого проводник ещё долго
    /// показывает прежний список «Открыть с помощью» и прежние значки.
    /// </summary>
    private static void NotifyShell() => SHChangeNotify(ShcneAssocChanged, ShcnfIdList, IntPtr.Zero, IntPtr.Zero);

    private const int ShcneAssocChanged = 0x08000000;
    private const uint ShcnfIdList = 0x0000;

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}
