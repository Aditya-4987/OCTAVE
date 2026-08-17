using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Octave.Core.Helpers;

public static class ShellRecycleBin
{
    private const int FO_DELETE = 0x0003;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_SILENT = 0x0004;

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

        try
        {
            string fullPath = Path.GetFullPath(filePath);

            var shf = new SHFILEOPSTRUCT
            {
                hwnd = IntPtr.Zero,
                wFunc = FO_DELETE,
                pFrom = fullPath + '\0' + '\0', // Double null-terminated string required by SHFileOperation
                pTo = null,
                fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT,
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
