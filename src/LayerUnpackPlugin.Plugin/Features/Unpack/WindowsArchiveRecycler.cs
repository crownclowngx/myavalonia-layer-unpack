using System.Runtime.InteropServices;
using LayerUnpackPlugin.Headless.Application;
using LayerUnpackPlugin.Headless.Infrastructure;

namespace LayerUnpackPlugin.Features.Unpack;

internal sealed class WindowsArchiveRecycler : IArchiveRecycler
{
    public void Recycle(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        PathPolicy.EnsureNoLinks(path);
        var root = Path.GetPathRoot(path)!;
        var info = new RecycleBinInfo { Size = Marshal.SizeOf<RecycleBinInfo>() };
        if (new DriveInfo(root).DriveType != DriveType.Fixed || SHQueryRecycleBin(root, ref info) < 0)
            throw new IOException("源所在的驱动器无法使用回收站。");

        var clsid = new Guid("3AD05575-8857-4850-9277-11B85BDB8E09");
        var shellItemId = typeof(IShellItem).GUID;
        var operation = (IFileOperation)Activator.CreateInstance(Type.GetTypeFromCLSID(clsid, throwOnError: true)!)!;
        IShellItem? item = null;
        try
        {
            Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path, IntPtr.Zero, ref shellItemId, out item));
            const uint recycleOnDelete = 0x00080000;
            const uint warnIfPermanent = 0x4000;
            const uint noErrorUi = 0x0400;
            const uint silentProgress = 0x0004;
            operation.SetOperationFlags(recycleOnDelete | warnIfPermanent | noErrorUi | silentProgress);
            operation.DeleteItem(item, IntPtr.Zero);
            operation.PerformOperations();
            operation.GetAnyOperationsAborted(out var aborted);
            if (aborted || File.Exists(path)) throw new IOException("源压缩包未能移入回收站。");
        }
        finally
        {
            if (item is not null) Marshal.FinalReleaseComObject(item);
            Marshal.FinalReleaseComObject(operation);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RecycleBinInfo { public int Size; public long Bytes; public long Items; }

    [DllImport("shell32.dll", EntryPoint = "SHQueryRecycleBinW", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string root, ref RecycleBinInfo info);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr binding, ref Guid id,
        out IShellItem item);

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem { }

    // Slots through GetAnyOperationsAborted follow the Windows SDK IFileOperation vtable.
    [ComImport, Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        void Advise(IntPtr sink, out uint cookie);
        void Unadvise(uint cookie);
        void SetOperationFlags(uint flags);
        void SetProgressMessage(IntPtr message);
        void SetProgressDialog(IntPtr dialog);
        void SetProperties(IntPtr properties);
        void SetOwnerWindow(IntPtr window);
        void ApplyPropertiesToItem(IntPtr item);
        void ApplyPropertiesToItems(IntPtr items);
        void RenameItem(IntPtr item, IntPtr name, IntPtr sink);
        void RenameItems(IntPtr items, IntPtr name);
        void MoveItem(IntPtr item, IntPtr destination, IntPtr name, IntPtr sink);
        void MoveItems(IntPtr items, IntPtr destination);
        void CopyItem(IntPtr item, IntPtr destination, IntPtr name, IntPtr sink);
        void CopyItems(IntPtr items, IntPtr destination);
        void DeleteItem(IShellItem item, IntPtr sink);
        void DeleteItems(IntPtr items);
        void NewItem(IntPtr destination, uint attributes, IntPtr name, IntPtr template, IntPtr sink);
        void PerformOperations();
        void GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }
}
