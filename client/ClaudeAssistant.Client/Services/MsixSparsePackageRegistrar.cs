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
// Живой тест показал, что даже при установке через machine-wide MSI (UAC при
// установке) сам install-hook (`--veloapp-install`) выполняется НЕ в повышенном
// контексте — реестр (HKCU, не требует прав) писался нормально, а
// LocalMachine\Root и Add-AppxPackage — нет, при том что вызывающий процесс
// оказывался неэлевированным. Поэтому здесь не просто проверка "уже
// администратор ли я", а самостоятельный релонч через `Verb = "runas"` —
// отдельный UAC-запрос именно на этот шаг. Вызывается только из
// OnAfterInstallFastCallback (не из OnAfterUpdateFastCallback) — сертификат и
// пакет нужно поставить один раз, а не дёргать UAC при каждом тихом
// автообновлении.
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

            if (IsAlreadyRegistered())
            {
                return;
            }

            var script =
                $"certutil.exe -addstore -f Root \"{certPath}\"; "
                + $"Add-AppxPackage -Path \"{msixPath}\" -ExternalLocation \"{installDir}\"";

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
        catch (Exception)
        {
            // Верхний уровень меню — украшение поверх уже работающего классического
            // пункта меню, не критичная для запуска приложения функциональность.
            // Сюда же попадает отказ пользователя от UAC-запроса (Win32Exception 1223) —
            // ничего страшного, просто продолжаем без верхнего уровня меню.
        }
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
                    + "\"if (Get-AppxPackage -Name 'ClaudeAssistant.ShellExtension') { 'True' } else { 'False' }\"",
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
