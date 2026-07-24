using System.Diagnostics;

namespace ClaudeAssistant.Client.Services;

// Регистрирует sparse MSIX-пакет (см. msix-sparse/AppxManifest.xml), который даёт
// пункту меню Проводника попасть в верхний уровень на Windows 11, а не только в
// "Показать больше опций" — см. IMPLEMENTATION_CHECKLIST.md, Этап 11. Работает
// поверх уже зарегистрированного классическим способом IExplorerCommand
// (см. ShellExtensionRegistrar) — это не альтернатива, а дополнение.
//
// Требует прав администратора: доверие самоподписанному сертификату добавляется
// в LocalMachine-хранилище (подтверждено эмпирически — CurrentUser хранилище
// Add-AppxPackage не принимает, отдаёт 0x800B0109).
//
// Живой тест показал, что install-hook (`--veloapp-install`) не всегда
// оказывается элевированным сам по себе (ни под MSI, ни через программный
// Verb=runas изнутри хука — судя по всему, UAC-запрос из фонового
// install-хука не всегда успевает корректно отрендериться пользователю).
// Поэтому основной способ получить права администратора теперь — манифест
// requireAdministrator, встроенный прямо в Setup.exe после `vpk pack` (см.
// релизный пайплайн / IMPLEMENTATION_CHECKLIST.md) — весь процесс установки,
// включая install-hook, идёт уже в повышенном контексте. Self-elevation
// через Verb=runas оставлен как запасной вариант на случай, если по какой-то
// причине элевация из манифеста не унаследовалась.
public static class MsixSparsePackageRegistrar
{
    private const string PackageName = "ClaudeAssistant.ShellExtension";

    public static void Register()
    {
        try
        {
            var installDir = AppContext.BaseDirectory;
            var certPath = Path.Combine(installDir, "ShellExtension", "ClaudeAssistant.cer");
            var msixPath = Path.Combine(installDir, "ShellExtension", "ClaudeAssistant.ShellExtension.msix");

            if (!File.Exists(certPath) || !File.Exists(msixPath))
            {
                return;
            }

            if (IsAlreadyRegistered())
            {
                return;
            }

            var script =
                $"certutil.exe -addstore -f Root \"{certPath}\"; "
                + $"Add-AppxPackage -Path \"{msixPath}\" -ExternalLocation \"{installDir}\"";

            RunElevatedIfNeeded(script);
        }
        catch (Exception)
        {
            // Верхний уровень меню — украшение поверх уже работающего классического
            // пункта меню, не критичная для запуска приложения функциональность.
        }
    }

    // Повторная регистрация sparse-пакета на ОБНОВЛЕНИИ. Velopack при апдейте
    // заменяет файлы приложения (comhost.dll и папку current), и привязка уже
    // зарегистрированного sparse-пакета к ним становится нерабочей — пункт верхнего
    // уровня меню Windows 11 пропадает, пока не выполнен повторный Add-AppxPackage
    // (тот же симптом ловился и при ручной переустановке, см. InstallerLauncher).
    //
    // В отличие от Register(): (1) НЕ трогаем LocalMachine-сертификат и HKLM
    // AppModelUnlock — они выставлены при установке и переживают обновления, значит
    // элевация/UAC здесь НЕ нужны (per-user Add-AppxPackage прав администратора не
    // требует, когда сертификат уже доверен и sideload включён); (2) НЕ пропускаем
    // по IsAlreadyRegistered — смысл именно в том, чтобы освежить привязку у уже
    // зарегистрированного пакета к новым файлам. Тихо и без UAC, поэтому подходит
    // для каждого автообновления.
    public static void ReRegister()
    {
        try
        {
            var installDir = AppContext.BaseDirectory;
            var msixPath = Path.Combine(installDir, "ShellExtension", "ClaudeAssistant.ShellExtension.msix");

            if (!File.Exists(msixPath))
            {
                return;
            }

            RunHidden($"Add-AppxPackage -Path \"{msixPath}\" -ExternalLocation \"{installDir}\"");
        }
        catch (Exception)
        {
            // Верхний уровень меню — украшение поверх работающего классического
            // пункта; сбой перерегистрации не критичен для работы приложения.
        }
    }

    // Вызывается из OnBeforeUninstallFastCallback — без этого удаление через
    // "Установка и удаление программ" оставляло бы зарегистрированный
    // sparse-пакет и доверенный сертификат висеть в системе даже после
    // полного удаления самого приложения.
    public static void Unregister()
    {
        try
        {
            var script =
                $"Get-AppxPackage -Name '{PackageName}' | Remove-AppxPackage -ErrorAction SilentlyContinue; "
                + "certutil.exe -delstore Root \"ClaudeAssistant\"";

            RunElevatedIfNeeded(script);
        }
        catch (Exception)
        {
            // Неудачная очистка не должна помешать остальному процессу удаления.
        }
    }

    private static void RunElevatedIfNeeded(string script)
    {
        if (IsElevated())
        {
            RunHidden(script);
            return;
        }

        var psi = new ProcessStartInfo("powershell.exe")
        {
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command \"{script}\"",
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        using var process = Process.Start(psi);
        process?.WaitForExit(60_000);
    }

    private static void RunHidden(string script)
    {
        using var process = Process.Start(
            new ProcessStartInfo("powershell.exe")
            {
                Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{script}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            }
        );
        process?.WaitForExit(60_000);
    }

    private static bool IsElevated()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var principal = new System.Security.Principal.WindowsPrincipal(identity);
        return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    // Не дёргаем UAC повторно при каждой установке/переустановке, если пакет уже
    // зарегистрирован — сертификат и sparse-пакет одноразовые по своей природе,
    // повторный Add-AppxPackage с тем же ExternalLocation просто не нужен.
    private static bool IsAlreadyRegistered()
    {
        try
        {
            var psi = new ProcessStartInfo("powershell.exe")
            {
                Arguments =
                    "-NoProfile -NonInteractive -Command "
                    + $"\"if (Get-AppxPackage -Name '{PackageName}') {{ 'True' }} else {{ 'False' }}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            };
            using var process = Process.Start(psi);
            var output = process?.StandardOutput.ReadToEnd().Trim();
            process?.WaitForExit(10_000);
            return output == "True";
        }
        catch (Exception)
        {
            return false;
        }
    }
}
