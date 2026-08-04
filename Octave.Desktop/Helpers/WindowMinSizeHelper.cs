using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace Octave_Desktop.Helpers;

public static class WindowMinSizeHelper
{
    private delegate IntPtr SubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, IntPtr uIdSubclass, IntPtr dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, IntPtr uIdSubclass, IntPtr dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private const uint WM_GETMINMAXINFO = 0x0024;
    private static SubclassProc? _subclassProc;

    public static void SetMinSize(Window window, int minWidth, int minHeight)
    {
        IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        _subclassProc = new SubclassProc((hWnd, uMsg, wParam, lParam, uIdSubclass, dwRefData) => 
        {
            if (uMsg == WM_GETMINMAXINFO)
            {
                uint dpi = GetDpiForWindow(hWnd);
                double scaleFactor = dpi / 96.0;

                MINMAXINFO minMaxInfo = Marshal.PtrToStructure<MINMAXINFO>(lParam);
                minMaxInfo.ptMinTrackSize.x = (int)(minWidth * scaleFactor);
                minMaxInfo.ptMinTrackSize.y = (int)(minHeight * scaleFactor);
                Marshal.StructureToPtr(minMaxInfo, lParam, true);
                
                return IntPtr.Zero;
            }
            return DefSubclassProc(hWnd, uMsg, wParam, lParam);
        });

        SetWindowSubclass(hwnd, _subclassProc, (IntPtr)1, IntPtr.Zero);
    }
}
