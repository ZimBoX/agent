using System.Diagnostics;
using System.Runtime.InteropServices;
using ClaudeAssistant.ShellExtension.Interop;
using Microsoft.Win32;

namespace ClaudeAssistant.ShellExtension;

// CLSID этого класса — регистрируется в реестре (InprocServer32 -> comhost.dll).
// Отдельный GUID для GetCanonicalName — по спецификации IExplorerCommand это
// самостоятельный идентификатор команды, не обязан совпадать с CLSID.
[ComVisible(true)]
[Guid("B932951B-8D25-4B01-9CD6-C0AB6801A39E")]
[ClassInterface(ClassInterfaceType.None)]
public class SendToClaudeCommand : IExplorerCommand
{
    private static readonly Guid CanonicalCommandGuid = new("C5E6AEB4-3E86-4095-AE6C-836D7F287CCC");

    public void GetTitle(IShellItemArray? psiItemArray, out string ppszName)
    {
        ppszName = "Отправить в Claude Assistant";
    }

    public void GetIcon(IShellItemArray? psiItemArray, out string ppszIcon)
    {
        // Без кастомной иконки в MVP — Explorer покажет иконку по умолчанию.
        ppszIcon = string.Empty;
        throw new NotImplementedException();
    }

    public void GetToolTip(IShellItemArray? psiItemArray, out string ppszInfotip)
    {
        ppszInfotip = string.Empty;
        throw new NotImplementedException();
    }

    public void GetCanonicalName(out Guid pguidCommandName)
    {
        pguidCommandName = CanonicalCommandGuid;
    }

    public void GetState(IShellItemArray? psiItemArray, bool fOkToBeSlow, out EXPCMDSTATE pCmdState)
    {
        pCmdState = EXPCMDSTATE.ECS_ENABLED;
    }

    public void Invoke(IShellItemArray? psiItemArray, IntPtr pbc)
    {
        // Любое необработанное исключение здесь всплывёт в explorer.exe — вместо
        // падения Проводника молча проглатываем ошибку. Логировать в файл (не в
        // консоль — у shell-хендлера её нет) для последующей диагностики.
        try
        {
            var paths = ExtractPaths(psiItemArray);
            if (paths.Count == 0)
            {
                return;
            }

            LaunchClient(paths);
        }
        catch (Exception ex)
        {
            TryLogError(ex);
        }
    }

    public void GetFlags(out EXPCMDFLAGS pFlags)
    {
        pFlags = EXPCMDFLAGS.ECF_DEFAULT;
    }

    public void EnumSubCommands(out IntPtr ppEnum)
    {
        // Подкоманд нет.
        ppEnum = IntPtr.Zero;
    }

    private static List<string> ExtractPaths(IShellItemArray? items)
    {
        var result = new List<string>();
        if (items is null)
        {
            return result;
        }

        items.GetCount(out var count);
        for (uint i = 0; i < count; i++)
        {
            items.GetItemAt(i, out var item);
            try
            {
                item.GetDisplayName(SIGDN.FileSysPath, out var ptr);
                try
                {
                    var path = Marshal.PtrToStringUni(ptr);
                    if (!string.IsNullOrEmpty(path))
                    {
                        result.Add(path);
                    }
                }
                finally
                {
                    Marshal.FreeCoTaskMem(ptr);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(item);
            }
        }

        return result;
    }

    private static void LaunchClient(IReadOnlyList<string> paths)
    {
        var exePath = GetClientExePath();
        if (exePath is null || !File.Exists(exePath))
        {
            TryLogError(new FileNotFoundException("Не найден путь к ClaudeAssistant.Client.exe в реестре"));
            return;
        }

        var arguments = string.Join(" ", paths.Select(p => $"\"{p}\""));
        Process.Start(new ProcessStartInfo(exePath, arguments) { UseShellExecute = true });
    }

    // Путь к клиенту пишется в реестр установщиком (сейчас — скриптом регистрации),
    // а не хардкодится и не вычисляется относительно расположения самой DLL —
    // это надёжнее при реальной установке в Program Files.
    private static string? GetClientExePath()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\ClaudeAssistant");
        return key?.GetValue("ClientExePath") as string;
    }

    private static void TryLogError(Exception ex)
    {
        try
        {
            var logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ClaudeAssistant",
                "shell-extension-error.log"
            );
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            File.AppendAllText(logPath, $"{DateTimeOffset.Now:O}  {ex}\n\n");
        }
        catch
        {
            // Если не получилось даже залогировать — ничего не поделать, но это
            // точно не должно всплыть в explorer.exe.
        }
    }
}
