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
            // Без хвостового разделителя — тем же путём регистрирует установщик, и
            // ExternalLocation должен совпадать при любой перерегистрации (иначе
            // 0x80073D0B, см. ReRegister).
            var installDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
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
    // Путь диагностического лога перерегистрации — %LocalAppData%\ClaudeAssistant\
    // menu-register.log. Временное: пока выясняем, почему Add-AppxPackage,
    // порождённый процессом приложения, не срабатывает, тогда как та же команда
    // из обычного PowerShell в сессии пользователя — срабатывает.
    private static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClaudeAssistant",
        "menu-register.log"
    );

    private static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch
        {
            // Лог — best-effort, его сбой не должен ничего ронять.
        }
    }

    public static void ReRegister()
    {
        try
        {
            // ВАЖНО: ExternalLocation обязан ПОБАЙТОВО совпадать с тем, чем пакет уже
            // зарегистрирован, иначе Add-AppxPackage падает с 0x80073D0B ("уже
            // установлен с другим внешним расположением"). Установщик
            // (InstallerLauncher) и ручная команда регистрируют по пути БЕЗ хвостового
            // разделителя (Path.Combine его не добавляет), а AppContext.BaseDirectory
            // возвращает путь С хвостовым '\' — срезаем, чтобы совпало.
            var installDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var msixPath = Path.Combine(installDir, "ShellExtension", "ClaudeAssistant.ShellExtension.msix");

            Log($"ReRegister start: installDir={installDir}; msixExists={File.Exists(msixPath)}; elevated={IsElevated()}");

            if (!File.Exists(msixPath))
            {
                Log("ReRegister: msix не найден — выход");
                return;
            }

            // Одинарные кавычки вокруг путей внутри -Command, чтобы не мучиться с
            // экранированием (пути в %LocalAppData% без кавычек тоже обычно ок).
            var psi = new ProcessStartInfo("powershell.exe")
            {
                Arguments =
                    "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "
                    + $"\"Add-AppxPackage -Path '{msixPath}' -ExternalLocation '{installDir}'\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using var process = Process.Start(psi);
            var stdout = process?.StandardOutput.ReadToEnd();
            var stderr = process?.StandardError.ReadToEnd();
            process?.WaitForExit(60_000);

            Log($"ReRegister done: exit={process?.ExitCode}; stdout={stdout?.Trim()}; stderr={stderr?.Trim()}");
        }
        catch (Exception ex)
        {
            Log($"ReRegister EXCEPTION: {ex}");
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
