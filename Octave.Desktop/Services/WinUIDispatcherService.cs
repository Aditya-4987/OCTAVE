using System;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Octave.Core.Interfaces;

namespace Octave_Desktop.Services;

/// <summary>
/// WinUI 3 implementation of IDispatcherService backed by Microsoft.UI.Dispatching.DispatcherQueue.
/// </summary>
public class WinUIDispatcherService : IDispatcherService
{
    private readonly DispatcherQueue _dispatcherQueue;

    public WinUIDispatcherService(DispatcherQueue dispatcherQueue)
    {
        _dispatcherQueue = dispatcherQueue ?? throw new ArgumentNullException(nameof(dispatcherQueue));
    }

    public bool IsOnUIThread => _dispatcherQueue.HasThreadAccess;

    public void ExecuteOnUIThread(Action action)
    {
        if (action == null) return;

        if (_dispatcherQueue.HasThreadAccess)
        {
            action();
        }
        else
        {
            _dispatcherQueue.TryEnqueue(() => action());
        }
    }

    public Task ExecuteOnUIThreadAsync(Func<Task> action)
    {
        if (action == null) return Task.CompletedTask;

        if (_dispatcherQueue.HasThreadAccess)
        {
            return action();
        }

        var tcs = new TaskCompletionSource();
        bool enqueued = _dispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                await action();
                tcs.TrySetResult();
            }
            catch (OperationCanceledException)
            {
                tcs.TrySetCanceled();
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });

        if (!enqueued)
        {
            tcs.TrySetCanceled();
        }

        return tcs.Task;
    }
}
