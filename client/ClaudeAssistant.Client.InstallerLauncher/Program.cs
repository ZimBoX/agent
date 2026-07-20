using System.Diagnostics;

// Ищет реальный Velopack Setup.exe рядом с собой по маске — не хардкодит канал
// (win/beta), чтобы один и тот же лаунчер годился для обеих сборок без пересборки.
var installerDir = AppContext.BaseDirectory;
var setupExePath = Directory.GetFiles(installerDir, "ClaudeAssistant.Client-*-Setup.exe").FirstOrDefault();

if (setupExePath is null)
{
    Console.WriteLine("Не найден файл установки (ClaudeAssistant.Client-*-Setup.exe) рядом с этим лаунчером.");
    Console.WriteLine("Нажмите любую клавишу для выхода...");
    Console.ReadKey();
    return 1;
}

// UseShellExecute=true — дочерний процесс наследует токен уже полученной
// здесь элевации, повторного запроса UAC для самого Setup.exe не будет.
using (var process = Process.Start(new ProcessStartInfo(setupExePath) { UseShellExecute = true }))
{
    process?.WaitForExit();
}

// Живой тест показал: Setup.exe деэлевируется перед тем, как запустить
// свежеустановленное приложение для отработки install-хука
// (OnAfterInstallFastCallback) — это объясняет, почему классическая
// регистрация (HKCU, не требует прав) отрабатывала, а MSIX sparse-пакет для
// верхнего уровня меню (требует LocalMachine-хранилище сертификатов) — нет,
// даже несмотря на элевацию самого Setup.exe. Поэтому эту часть делаем прямо
// здесь — это единственный процесс во всей цепочке, для которого элевация
// подтверждена реальным кликом пользователя по UAC, а не унаследована.
RegisterTopLevelMenu();
return 0;

static void RegisterTopLevelMenu()
{
    var currentDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClaudeAssistant.Client",
        "current"
    );
    var certPath = Path.Combine(currentDir, "ShellExtension", "ClaudeAssistant.cer");
    var msixPath = Path.Combine(currentDir, "ShellExtension", "ClaudeAssistant.ShellExtension.msix");

    // Setup.exe уже дождался своего дочернего процесса выше, но сам install-хук
    // внутри приложения может доделывать запись файлов на диск чуть позже —
    // короткий поллинг вместо жёсткой гонки.
    for (var i = 0; i < 20 && !(File.Exists(certPath) && File.Exists(msixPath)); i++)
    {
        Thread.Sleep(500);
    }

    if (!File.Exists(certPath) || !File.Exists(msixPath))
    {
        Console.WriteLine("Файлы sparse-пакета не найдены после установки — пункт верхнего уровня меню пропущен.");
        return;
    }

    if (IsAlreadyRegistered())
    {
        return;
    }

    var script =
        $"certutil.exe -addstore -f Root \"{certPath}\"; "
        + $"Add-AppxPackage -Path \"{msixPath}\" -ExternalLocation \"{currentDir}\"";

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
    var stderr = process?.StandardError.ReadToEnd();
    process?.WaitForExit(60_000);

    if (process is { ExitCode: not 0 })
    {
        Console.WriteLine($"Регистрация верхнего уровня меню завершилась с ошибкой: {stderr}");
    }
}

static bool IsAlreadyRegistered()
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
