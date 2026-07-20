using System.Diagnostics;
using System.Security.Principal;

namespace ClaudeAssistant.Client.Services;

// Регистрирует sparse MSIX-пакет (см. msix-sparse/AppxManifest.xml), который даёт
// пункту меню Проводника попасть в верхний уровень на Windows 11, а не только в
// "Показать больше опций" — см. IMPLEMENTATION_CHECKLIST.md, Этап 11. Работает
// поверх уже зарегистрированного классическим способом IExplorerCommand
// (см. ShellExtensionRegistrar) — это не альтернатива, а дополнение.
//
// Требует прав администратора: доверие самоподписанному сертификату
// добавляется в LocalMachine-хранилище (подтверждено эмпирически — CurrentUser
// хранилище Add-AppxPackage не принимает, отдаёт 0x800B0109). Поэтому вызывается
// из install-hook только при установке через machine-wide MSI (vpk pack --msi),
// который сам запрашивает UAC один раз при установке. При портативной установке
// (Setup.exe без --msi, без элевации) этот шаг тихо пропускается — верхний
// уровень меню не появится, но само приложение и классический пункт меню
// продолжают работать нормально.
public static class MsixSparsePackageRegistrar
{
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

            if (!IsElevated())
            {
                // Без прав администратора certutil -addstore Root и
                // Add-AppxPackage -ExternalLocation всё равно откажут — не тратим
                // время на заведомо обречённые попытки и не показываем пользователю
                // непонятные ошибки за спиной.
                return;
            }

            RunHidden("certutil.exe", $"-addstore -f Root \"{certPath}\"");

            RunHidden(
                "powershell.exe",
                $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command " +
                    $"\"Add-AppxPackage -Path '{msixPath}' -ExternalLocation '{installDir}'\""
            );
        }
        catch (Exception)
        {
            // Верхний уровень меню — украшение поверх уже работающего классического
            // пункта меню, не критичная для запуска приложения функциональность.
        }
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static void RunHidden(string fileName, string arguments)
    {
        using var process = Process.Start(
            new ProcessStartInfo(fileName, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            }
        );
        process?.WaitForExit(30_000);
    }
}
