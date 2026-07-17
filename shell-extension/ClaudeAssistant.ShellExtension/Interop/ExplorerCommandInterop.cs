using System.Runtime.InteropServices;

namespace ClaudeAssistant.ShellExtension.Interop;

[Flags]
public enum EXPCMDSTATE : uint
{
    ECS_ENABLED = 0x0,
    ECS_DISABLED = 0x1,
    ECS_HIDDEN = 0x2,
}

[Flags]
public enum EXPCMDFLAGS : uint
{
    ECF_DEFAULT = 0x0,
}

// Этот интерфейс реализуем МЫ (Explorer вызывает нас) — поэтому без [ComImport],
// только атрибуты, нужные для генерации CCW (COM Callable Wrapper). Порядок
// методов и здесь должен точно соответствовать shobjidl_core.h.
[ComVisible(true)]
[Guid("A08CE4D0-FA25-44AB-B57C-C7B1C323E0B9")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IExplorerCommand
{
    void GetTitle(IShellItemArray? psiItemArray, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
    void GetIcon(IShellItemArray? psiItemArray, [MarshalAs(UnmanagedType.LPWStr)] out string ppszIcon);
    void GetToolTip(IShellItemArray? psiItemArray, [MarshalAs(UnmanagedType.LPWStr)] out string ppszInfotip);
    void GetCanonicalName(out Guid pguidCommandName);
    void GetState(IShellItemArray? psiItemArray, [MarshalAs(UnmanagedType.Bool)] bool fOkToBeSlow, out EXPCMDSTATE pCmdState);
    void Invoke(IShellItemArray? psiItemArray, IntPtr pbc);
    void GetFlags(out EXPCMDFLAGS pFlags);
    void EnumSubCommands(out IntPtr ppEnum);
}
