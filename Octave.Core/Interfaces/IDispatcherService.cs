using System;
using System.Threading.Tasks;

namespace Octave.Core.Interfaces;

/// <summary>
/// Platform-independent UI dispatcher abstraction.
/// Enables core playback and queue services to marshal state mutations to the UI thread
/// without depending on platform-specific UI frameworks (WinUI, macOS, Linux).
/// </summary>
public interface IDispatcherService
{
    /// <summary>
    /// Executes the specified action on the UI thread.
    /// If already on the UI thread, executes immediately; otherwise enqueues to the UI thread.
    /// </summary>
    void ExecuteOnUIThread(Action action);

    /// <summary>
    /// Executes the specified asynchronous function on the UI thread.
    /// </summary>
    Task ExecuteOnUIThreadAsync(Func<Task> action);

    /// <summary>
    /// Gets a value indicating whether the current execution thread is the UI thread.
    /// </summary>
    bool IsOnUIThread { get; }
}
