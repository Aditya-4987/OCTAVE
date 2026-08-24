using System;
using System.IO;

namespace Octave_Desktop.Helpers;

// NF-32: persistent diagnostics. The 0xC000027B stowed-exception crash left
// nothing but a flood of unrelated first-chance lines in the debugger output -
// and nothing at all when no debugger is attached. These appends survive that
// so the next failure names its site.
public static class CrashLog
{
    private static readonly object Gate = new();
    private static string? _logDir;

    public static void Write(string source, Exception? exception)
    {
        try
        {
            string dir = _logDir ??= Path.Combine(
                Windows.Storage.ApplicationData.Current.LocalFolder.Path, "logs");
            lock (Gate)
            {
                Directory.CreateDirectory(dir);
                File.AppendAllText(
                    Path.Combine(dir, $"diagnostics-{DateTime.UtcNow:yyyyMMdd}.log"),
                    $"{DateTime.UtcNow:HH:mm:ss.fff} [{source}]{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
            }
        }
        catch
        {
            // Diagnostics must never throw - not even here.
        }
    }
}
