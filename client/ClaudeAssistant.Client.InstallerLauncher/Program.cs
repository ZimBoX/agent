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
using var process = Process.Start(new ProcessStartInfo(setupExePath) { UseShellExecute = true });
process?.WaitForExit();
return process?.ExitCode ?? 0;
