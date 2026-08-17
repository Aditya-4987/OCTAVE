using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Octave.Core.Helpers;

public static class ShellRecycleBin
{
    private const uint FOF_ALLOWUNDO = 0x0040;
    private const uint FOF_NOCONFIRMATION = 0x0010;
    private const uint FOF_SILENT = 0x0004;
    private const uint FOF_NOERRORUI = 0x0400;
    private const uint FOFX_RECYCLEONDELETE = 0x00080000;

    [ComImport]
    [Guid("3ad05575-8857-4850-9277-11b85bdb8e09")]
    [ClassInterface(ClassInterfaceType.None)]
    private class FileOperation { }

    [ComImport]
    [Guid("947aab5f-0a5c-4c13-b4d6-4bf503683880")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        uint Advise(IntPtr pfops, out uint pdwCookie);
        void Unadvise(uint dwCookie);
        void SetOperationFlags(uint dwOperationFlags);
        void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string pszMessage);
        void SetProgressDialog(IntPtr popd);
        void SetOwnerWindow(IntPtr hwndOwner);
        void ApplyPropertiesToItem(IShellItem psi);
        void ApplyPropertiesToItems(IntPtr punkItems);
        void RenameItem(IShellItem psiItem, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName, IntPtr pfopsItem);
        void RenameItems(IntPtr pUnkItems, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName);
        void MoveItem(IShellItem psiItem, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName, IntPtr pfopsItem);
        void MoveItems(IntPtr punkItems, IShellItem psiDestinationFolder);
        void CopyItem(IShellItem psiItem, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string pszCopyName, IntPtr pfopsItem);
        void CopyItems(IntPtr punkItems, IShellItem psiDestinationFolder);
        void DeleteItem(IShellItem psiItem, IntPtr pfopsItem);
        void DeleteItems(IntPtr punkItems);
        void NewItem(IShellItem psiDestinationFolder, uint dwFileAttributes, [MarshalAs(UnmanagedType.LPWStr)] string pszName, [MarshalAs(UnmanagedType.LPWStr)] string pszTemplateName, IntPtr pfopsItem);
        void PerformOperations();
        [return: MarshalAs(UnmanagedType.Bool)]
        bool GetAnyOperationsAborted();
    }

    [ComImport]
    [Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        IntPtr pbc,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        out IShellItem ppv);

    private const int FO_DELETE = 0x0003;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? pTo;
        public ushort fFlags;
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT FileOp);

    public static bool SendToRecycleBin(string filePath)
    {
        if (!OperatingSystem.IsWindows())
            return false;

        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return false;

        string fullPath = Path.GetFullPath(filePath);

        // Try modern Windows 10/11 COM IFileOperation first for long path (>260 chars) support
        IShellItem? shellItem = null;
        IFileOperation? fileOp = null;
        try
        {
            int hr = SHCreateItemFromParsingName(fullPath, IntPtr.Zero, typeof(IShellItem).GUID, out shellItem);
            if (hr == 0 && shellItem != null)
            {
                fileOp = (IFileOperation)new FileOperation();
                fileOp.SetOperationFlags(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI | FOFX_RECYCLEONDELETE);
                fileOp.DeleteItem(shellItem, IntPtr.Zero);
                fileOp.PerformOperations();

                if (!fileOp.GetAnyOperationsAborted())
                {
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ShellRecycleBin] IFileOperation failed for '{filePath}': {ex.Message}. Falling back to SHFileOperation.");
        }
        finally
        {
            if (shellItem != null) Marshal.ReleaseComObject(shellItem);
            if (fileOp != null) Marshal.ReleaseComObject(fileOp);
        }

        // Fallback to legacy SHFileOperation if IFileOperation is unavailable
        try
        {
            var shf = new SHFILEOPSTRUCT
            {
                hwnd = IntPtr.Zero,
                wFunc = FO_DELETE,
                pFrom = fullPath + '\0' + '\0', // Double null-terminated string
                pTo = null,
                fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT),
                fAnyOperationsAborted = false,
                hNameMappings = IntPtr.Zero,
                lpszProgressTitle = null
            };

            int result = SHFileOperation(ref shf);
            if (result != 0 || shf.fAnyOperationsAborted)
            {
                System.Diagnostics.Debug.WriteLine($"[ShellRecycleBin] SHFileOperation failed with code {result}, aborted={shf.fAnyOperationsAborted} for '{filePath}'");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ShellRecycleBin] Exception recycling file '{filePath}': {ex.Message}");
            return false;
        }
    }
}
