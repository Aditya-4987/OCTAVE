using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace Octave_Desktop.Helpers;

public static class WindowMinSizeHelper
{
    private delegate IntPtr SubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, IntPtr uIdSubclass, IntPtr dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, IntPtr uIdSubclass, IntPtr dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, IntPtr uIdSubclass);

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
    private static readonly IntPtr SubclassId = new(1);

    // SYS-01: one delegate per hwnd, rooted here for exactly as long as its
    // subclass is installed. The old single static field was overwritten by
    // every SetMinSize call - with two windows, the first window's delegate
    // became unreachable garbage and its next WM_GETMINMAXINFO jumped into
    // collected memory (CallbackOnCollectedDelegate -> access violation). Each
    // closure also carries its own min sizes instead of silently sharing
    // whatever the most recent caller passed.
    private static readonly Dictionary<IntPtr, SubclassProc> _subclassesByHwnd = new();

    public static void SetMinSize(Window window, int minWidth, int minHeight)
    {
        IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);

        lock (_subclassesByHwnd)
        {
            // Re-invocation for an already-subclassed window swaps cleanly
            // instead of stacking a second link on comctl32's chain.
            if (_subclassesByHwnd.Remove(hwnd, out SubclassProc? existing))
            {
                RemoveWindowSubclass(hwnd, existing, SubclassId);
            }

            var proc = new SubclassProc((hWnd, uMsg, wParam, lParam, uIdSubclass, dwRefData) =>
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

            if (SetWindowSubclass(hwnd, proc, SubclassId, IntPtr.Zero))
            {
                // This dictionary entry is the GC root keeping the delegate
                // alive while the native subclass references it.
                _subclassesByHwnd[hwnd] = proc;
            }
        }
    }

    /// <summary>
    /// SYS-05: removes the subclass when its window closes so comctl32 never
    /// invokes into a destroyed window's stale delegate.
    /// </summary>
    public static void ClearMinSize(IntPtr hwnd)
    {
        lock (_subclassesByHwnd)
        {
            if (_subclassesByHwnd.Remove(hwnd, out SubclassProc? proc))
            {
                RemoveWindowSubclass(hwnd, proc, SubclassId);
            }
        }
    }
}
