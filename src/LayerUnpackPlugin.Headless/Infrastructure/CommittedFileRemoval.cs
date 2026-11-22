using System.Runtime.InteropServices;
using LayerUnpackPlugin.Headless.Application;
using LayerUnpackPlugin.Headless.Contracts;
using Microsoft.Win32.SafeHandles;

namespace LayerUnpackPlugin.Headless.Infrastructure;

internal static class CommittedFileRemoval
{
    internal static async Task RemoveAsync(OrganizationInput input, CancellationToken token)
    {
        if (OperatingSystem.IsWindows())
        {
            // Request read and delete access together. The verified handle, rather than a second path
            // lookup, is marked for deletion. Other writers and replacements are excluded until close.
            const uint readAndDelete = 0x80010000;
            const uint shareRead = 1;
            const uint openExisting = 3;
            const uint overlappedAndOpenReparsePoint = 0x40200000;
            const int fileDispositionInfo = 4;
            using var handle = CreateFile(input.Path, readAndDelete, shareRead, IntPtr.Zero, openExisting,
                overlappedAndOpenReparsePoint, IntPtr.Zero);
            if (handle.IsInvalid) throw new IOException("无法独占核对待清理文件。", Marshal.GetHRForLastWin32Error());
            await using var stream = new FileStream(handle, FileAccess.Read, 1, isAsync: true);
            OrganizationFiles.ValidateMetadata(input);
            await UnpackCollection.VerifyContentsAsync(stream, input.Entry, input.Path, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            byte delete = 1;
            if (!SetFileInformationByHandle(handle, fileDispositionInfo, ref delete, sizeof(byte)))
                throw new IOException("无法移除已核对的文件。", Marshal.GetHRForLastWin32Error());
            return;
        }

        await using (var stream = OrganizationFiles.OpenRead(input.Path))
            await UnpackCollection.VerifyContentsAsync(stream, input.Entry, input.Path, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        OrganizationFiles.ValidateMetadata(input);
        File.Delete(input.Path);
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr attributes,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int fileInformationClass,
        ref byte information, uint size);
}
