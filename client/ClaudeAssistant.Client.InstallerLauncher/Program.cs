using System.Diagnostics;

// Тонкий установщик: не несёт в себе payload приложения, а скачивает актуальный
// Velopack Setup.exe с backend-сервера и запускает его. Это избавляет от
// необходимости класть два файла рядом (лаунчер + Setup.exe) — распространяется
// только один этот exe. Манифест требует прав администратора (app.manifest,
// requireAdministrator) — они нужны и для install-хука, и для регистрации
// sparse-пакета верхнего меню (RegisterTopLevelMenu ниже).
//
// Канал и адрес сервера — константы сборки. Для беты и локального теста это
// localhost; при деплое на боевой сервер (Этап 12) меняется здесь на адрес
// компании. Имя Setup.exe детерминировано: `vpk pack --channel <ch>` выпускает
// ClaudeAssistant.Client-<ch>-Setup.exe, backend отдаёт его как статику из
// /releases (см. Program.cs бэкенда, UseStaticFiles для ReleasesPath).

const string backendBaseUrl = "http://192.168.0.15:5149";
const string channel = "beta";
var setupUrl = $"{backendBaseUrl}/releases/ClaudeAssistant.Client-{channel}-Setup.exe";

var setupExePath = Path.Combine(Path.GetTempPath(), $"ClaudeAssistant.Client-{channel}-Setup.exe");

Console.WriteLine($"Загрузка установщика с {setupUrl} ...");
try
{
    using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
    using var response = await http.GetAsync(setupUrl, HttpCompletionOption.ResponseHeadersRead);
    response.EnsureSuccessStatusCode();
    await using (var fs = File.Create(setupExePath))
    {
        await response.Content.CopyToAsync(fs);
    }
}
catch (Exception ex)
{
    Console.WriteLine($"Не удалось скачать установщик: {ex.Message}");
    Console.WriteLine("Проверьте подключение к серверу и попробуйте снова.");
    Console.WriteLine("Нажмите любую клавишу для выхода...");
    Console.ReadKey();
    return 1;
}

Console.WriteLine("Установка...");
// UseShellExecute=true — дочерний процесс наследует токен уже полученной здесь
// элевации, повторного запроса UAC для самого Setup.exe не будет.
using (var process = Process.Start(new ProcessStartInfo(setupExePath) { UseShellExecute = true }))
{
    process?.WaitForExit();
}

// Setup.exe (точнее, install-хук приложения) деэлевируется Velopack перед
// запуском, поэтому регистрацию sparse-пакета верхнего меню (нужны права
// администратора: LocalMachine-хранилище сертификатов + Add-AppxPackage) делаем
// здесь — это единственный процесс в цепочке с подтверждённой реальным кликом
// по UAC элевацией.
RegisterTopLevelMenu();

try
{
    File.Delete(setupExePath);
}
catch
{
    // Временный файл в TEMP — не критично, если не удалился.
}

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

    // Всегда перерегистрируем (не пропускаем при уже существующей регистрации):
    // версия sparse-пакета захардкожена 1.0.0.0 и не меняется между релизами,
    // поэтому Get-AppxPackage находит "тот же" пакет после переустановки с новым
    // comhost.dll — но пункт меню при этом пропадал, пока не выполнялся повторный
    // Add-AppxPackage. Он идемпотентен и быстр.
    //
    // AllowAllTrustedApps=1 — включает sideloading; без него Add-AppxPackage
    // -ExternalLocation отказывает с 0x80073D2E даже при верной элевации и
    // доверенном сертификате (подтверждено на реальной машине).
    var script =
        "reg add \"HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\AppModelUnlock\" "
        + "/v AllowAllTrustedApps /t REG_DWORD /d 1 /f; "
        + $"certutil.exe -addstore -f Root \"{certPath}\"; "
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
