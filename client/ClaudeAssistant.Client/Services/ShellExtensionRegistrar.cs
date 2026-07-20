using Microsoft.Win32;

namespace ClaudeAssistant.Client.Services;

// Автоматическая версия shell-extension/register.ps1 — вызывается из install-hook
// (VelopackApp в App.xaml.cs: OnAfterInstallFastCallback/OnAfterUpdateFastCallback),
// чтобы пункт "Отправить в Claude Assistant" появлялся в Проводнике сам, без
// ручного запуска PowerShell-скрипта после установки. Сам register.ps1 остаётся
// в репозитории для ручной отладки — здесь ровно та же логика, просто на C#, а
// не PowerShell, чтобы не зависеть от политики выполнения скриптов на машине.
// Идемпотентно: безопасно вызывать повторно на каждое обновление — перезаписывает
// те же самые значения.
public static class ShellExtensionRegistrar
{
    private const string ClsId = "{B932951B-8D25-4B01-9CD6-C0AB6801A39E}";

    public static void Register()
    {
        try
        {
            // Собирается и копируется в publish\ShellExtension\ отдельным шагом
            // релизного пайплайна (shell-extension — самостоятельный проект, не
            // ссылка из клиента) — см. IMPLEMENTATION_CHECKLIST.md, Этап 11.
            var comHostPath = Path.Combine(AppContext.BaseDirectory, "ShellExtension", "ClaudeAssistant.ShellExtension.comhost.dll");
            var clientExePath = Path.Combine(AppContext.BaseDirectory, "ClaudeAssistant.Client.exe");

            if (!File.Exists(comHostPath) || !File.Exists(clientExePath))
            {
                // Компонент почему-то не попал в поставку — не роняем установку/
                // обновление из-за отсутствующего пункта меню, это не критично
                // для работы основного приложения.
                return;
            }

            using (var claudeKey = Registry.CurrentUser.CreateSubKey(@"Software\ClaudeAssistant")!)
            {
                // Читается SendToClaudeCommand.GetClientExePath() на стороне shell extension.
                claudeKey.SetValue("ClientExePath", clientExePath);
            }

            using (var clsidKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\CLSID\{ClsId}")!)
            {
                clsidKey.SetValue(null, "ClaudeAssistant SendTo Command");
                using var inprocKey = clsidKey.CreateSubKey("InprocServer32")!;
                inprocKey.SetValue(null, comHostPath);
                inprocKey.SetValue("ThreadingModel", "Both");
            }

            RegisterShellVerb(@"Software\Classes\*\shell\ClaudeAssistant");
            RegisterShellVerb(@"Software\Classes\Directory\shell\ClaudeAssistant");
        }
        catch (Exception)
        {
            // Регистрация меню — второстепенная функция относительно запуска
            // самого приложения; ошибка здесь не должна помешать штатному старту
            // (тот же принцип, что и в catch-all у SendToClaudeCommand.Invoke).
        }
    }

    private static void RegisterShellVerb(string keyPath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(keyPath)!;
        key.SetValue(null, "Отправить в Claude Assistant");
        key.SetValue("ExplorerCommandHandler", ClsId);
    }

    // Автоматическая версия shell-extension/unregister.ps1 — вызывается из
    // OnBeforeUninstallFastCallback (App.xaml.cs). Без этого удаление через
    // "Установка и удаление программ" сносит только файлы, которые поставил
    // сам Velopack — про наши собственные ключи реестра (CLSID, *\shell,
    // Directory\shell) он не знает, и они оставались бы висеть в реестре
    // даже после полного удаления приложения.
    public static void Unregister()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\*\shell\ClaudeAssistant", throwOnMissingSubKey: false);
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\Directory\shell\ClaudeAssistant", throwOnMissingSubKey: false);
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\CLSID\{ClsId}", throwOnMissingSubKey: false);
        }
        catch (Exception)
        {
            // Неудачная очистка не должна помешать остальному процессу удаления.
        }
    }
}
