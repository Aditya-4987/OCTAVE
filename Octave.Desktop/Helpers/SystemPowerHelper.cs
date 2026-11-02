using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Octave.Core.Models;

namespace Octave_Desktop.Helpers;

public readonly record struct PowerActionResult(
    bool Success,
    string? ErrorMessage
);

/// <summary>
/// Safe Windows power management helper for sleep, non-forced shutdown, and non-forced sign-out.
/// Strictly avoids force flags to guarantee that Windows and open applications can save unsaved data.
/// </summary>
public static class SystemPowerHelper
{
    // --- powrprof.dll (Sleep / Standby) ---
    [DllImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetSuspendState(
        [MarshalAs(UnmanagedType.Bool)] bool hibernate,
        [MarshalAs(UnmanagedType.Bool)] bool forceCritical,
        [MarshalAs(UnmanagedType.Bool)] bool disableWakeEvent);

    // --- user32.dll (ExitWindowsEx) ---
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ExitWindowsEx(uint uFlags, uint dwReason);

    // Flags for ExitWindowsEx (NEVER include EWX_FORCE 0x00000004 or EWX_FORCEIFHUNG 0x00000010)
    private const uint EWX_LOGOFF   = 0x00000000;
    private const uint EWX_SHUTDOWN = 0x00000001;
    private const uint EWX_POWEROFF = 0x00000008;

    // Reason flags (Planned application maintenance)
    private const uint SHTDN_REASON_MAJOR_APPLICATION = 0x00040000;
    private const uint SHTDN_REASON_MINOR_MAINTENANCE = 0x00000001;
    private const uint SHTDN_REASON_FLAG_PLANNED      = 0x40000000;
    private const uint ShutdownReason = SHTDN_REASON_MAJOR_APPLICATION | SHTDN_REASON_MINOR_MAINTENANCE | SHTDN_REASON_FLAG_PLANNED;

    // --- advapi32.dll (Token Privilege Escalation for Shutdown) ---
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValue(string? lpSystemName, string lpName, out LUID lpLuid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(
        IntPtr tokenHandle,
        [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges,
        ref TOKEN_PRIVILEGES newState,
        uint bufferLength,
        IntPtr previousState,
        IntPtr returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID_AND_ATTRIBUTES
    {
        public LUID Luid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        public LUID_AND_ATTRIBUTES Privileges;
    }

    private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    private const uint TOKEN_QUERY             = 0x0008;
    private const uint SE_PRIVILEGE_ENABLED    = 0x00000002;
    private const string SE_SHUTDOWN_NAME      = "SeShutdownPrivilege";

    /// <summary>
    /// Event fired whenever OCTAVE initiates a power action (shutdown or sign out)
    /// so the main window can arm cancellation detection via WM_ENDSESSION.
    /// </summary>
    public static event EventHandler<SleepTimerCompletionAction>? PowerActionInitiated;

    /// <summary>
    /// Puts the system to sleep (Standby). Does not force closure of applications.
    /// </summary>
    public static PowerActionResult Sleep()
    {
        try
        {
            Debug.WriteLine("[SystemPowerHelper] Entering system standby (sleep)...");
            bool success = SetSuspendState(hibernate: false, forceCritical: false, disableWakeEvent: false);
            if (!success)
            {
                int err = Marshal.GetLastWin32Error();
                return new PowerActionResult(false, $"Failed to put PC to sleep (Win32 Error: {err})");
            }
            return new PowerActionResult(true, null);
        }
        catch (Exception ex)
        {
            return new PowerActionResult(false, ex.Message);
        }
    }

    /// <summary>
    /// Initiates a safe, non-forced PC shutdown.
    /// If another application requires attention to save work, Windows will veto the shutdown.
    /// </summary>
    public static PowerActionResult ShutdownSafe()
    {
        try
        {
            Debug.WriteLine("[SystemPowerHelper] Initiating safe non-forced shutdown...");
            EnableShutdownPrivilege();

            PowerActionInitiated?.Invoke(null, SleepTimerCompletionAction.ShutdownPC);

            // EWX_SHUTDOWN | EWX_POWEROFF without EWX_FORCE
            bool success = ExitWindowsEx(EWX_SHUTDOWN | EWX_POWEROFF, ShutdownReason);
            if (!success)
            {
                int err = Marshal.GetLastWin32Error();
                Debug.WriteLine($"[SystemPowerHelper] ExitWindowsEx failed with error {err}");
                return new PowerActionResult(false, $"Shutdown could not be started (Win32 Error: {err}). Another application may require attention.");
            }

            return new PowerActionResult(true, null);
        }
        catch (Exception ex)
        {
            return new PowerActionResult(false, ex.Message);
        }
    }

    /// <summary>
    /// Initiates a safe, non-forced Windows sign-out / logoff.
    /// If another application requires attention to save work, Windows will veto the logoff.
    /// </summary>
    public static PowerActionResult SignOutSafe()
    {
        try
        {
            Debug.WriteLine("[SystemPowerHelper] Initiating safe non-forced sign out...");

            PowerActionInitiated?.Invoke(null, SleepTimerCompletionAction.SignOut);

            // EWX_LOGOFF without EWX_FORCE
            bool success = ExitWindowsEx(EWX_LOGOFF, ShutdownReason);
            if (!success)
            {
                int err = Marshal.GetLastWin32Error();
                Debug.WriteLine($"[SystemPowerHelper] ExitWindowsEx logoff failed with error {err}");
                return new PowerActionResult(false, $"Sign out could not be started (Win32 Error: {err}). Another application may require attention.");
            }

            return new PowerActionResult(true, null);
        }
        catch (Exception ex)
        {
            return new PowerActionResult(false, ex.Message);
        }
    }

    /// <summary>
    /// Executes the specified completion action safely.
    /// </summary>
    public static PowerActionResult ExecuteAction(SleepTimerCompletionAction action)
    {
        return action switch
        {
            SleepTimerCompletionAction.StopPlayback => new PowerActionResult(true, null),
            SleepTimerCompletionAction.SleepPC => Sleep(),
            SleepTimerCompletionAction.ShutdownPC => ShutdownSafe(),
            SleepTimerCompletionAction.SignOut => SignOutSafe(),
            _ => new PowerActionResult(true, null)
        };
    }

    private static void EnableShutdownPrivilege()
    {
        IntPtr hToken = IntPtr.Zero;
        try
        {
            IntPtr currentProcess = Process.GetCurrentProcess().Handle;
            if (!OpenProcessToken(currentProcess, TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out hToken))
            {
                Debug.WriteLine($"[SystemPowerHelper] OpenProcessToken failed: {Marshal.GetLastWin32Error()}");
                return;
            }

            if (!LookupPrivilegeValue(null, SE_SHUTDOWN_NAME, out LUID luid))
            {
                Debug.WriteLine($"[SystemPowerHelper] LookupPrivilegeValue failed: {Marshal.GetLastWin32Error()}");
                return;
            }

            var tp = new TOKEN_PRIVILEGES
            {
                PrivilegeCount = 1,
                Privileges = new LUID_AND_ATTRIBUTES
                {
                    Luid = luid,
                    Attributes = SE_PRIVILEGE_ENABLED
                }
            };

            if (!AdjustTokenPrivileges(hToken, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero))
            {
                Debug.WriteLine($"[SystemPowerHelper] AdjustTokenPrivileges failed: {Marshal.GetLastWin32Error()}");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SystemPowerHelper] EnableShutdownPrivilege error: {ex.Message}");
        }
        finally
        {
            if (hToken != IntPtr.Zero)
            {
                CloseHandle(hToken);
            }
        }
    }
}
