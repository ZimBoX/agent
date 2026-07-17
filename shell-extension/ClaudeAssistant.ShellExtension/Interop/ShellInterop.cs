using System.Runtime.InteropServices;

namespace ClaudeAssistant.ShellExtension.Interop;

// Эти интерфейсы реализует сам Explorer — мы только вызываем их (RCW), поэтому
// [ComImport]. Порядок методов должен точно соответствовать shobjidl_core.h,
// иначе получим смещение в vtable и падение/мусорные данные.

public enum SIGDN : uint
{
    NormalDisplay = 0,
    FileSysPath = 0x80058000,
}

[ComImport]
[Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IShellItem
{
    void BindToHandler(
        IntPtr pbc,
        [MarshalAs(UnmanagedType.LPStruct)] Guid bhid,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        out IntPtr ppv
    );
    void GetParent(out IShellItem ppsi);
    void GetDisplayName(SIGDN sigdnName, out IntPtr ppszName);
    void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
    void Compare(IShellItem psi, uint hint, out int piOrder);
}

[ComImport]
[Guid("B63EA76D-1F85-456F-A19C-48159EFA858B")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IShellItemArray
{
    void BindToHandler(
        IntPtr pbc,
        [MarshalAs(UnmanagedType.LPStruct)] Guid bhid,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        out IntPtr ppvOut
    );
    void GetPropertyStore(int flags, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr ppv);
    void GetPropertyDescriptionList(IntPtr keyType, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr ppv);
    void GetAttributes(int attribFlags, uint sfgaoMask, out uint psfgaoAttribs);
    void GetCount(out uint pdwNumItems);
    void GetItemAt(uint dwIndex, out IShellItem ppsi);
    void EnumItems(out IntPtr ppenumShellItems);
}
