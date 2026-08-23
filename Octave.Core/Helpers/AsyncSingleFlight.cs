using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Octave.Core.Helpers;

/// <summary>
/// Deduplicates concurrent async work by key: the first caller executes the
/// factory; concurrent callers for the same key await the same underlying task.
///
/// Cancellation contract (SF-01): the shared factory never observes an
/// individual caller's token. Callers pass their token to ExecuteAsync and it
/// is applied ONLY to their personal wait (<c>task.WaitAsync(ct)</c>) — so one
/// caller cancelling can no longer corrupt or abort the shared fetch for the
/// others. The shared work is expected to be self-bounded (provider timeouts);
/// its result lands in the caller-level caches for later reuse.
///
/// Exception contract (HL-02): a factory fault propagates to every waiter that
/// joins before the entry is removed — deliberate, so a broken provider fails
/// fast for all instead of each waiter re-attempting serially. A continuation
/// observes the faulted task so an all-callers-cancelled race never surfaces
/// as UnobservedTaskException.
///
/// Dedup contract (SF-02): deduplication is best-effort. A caller whose lookup
/// lands after the leader finished and removed its entry simply becomes the
/// new leader; orchestrators re-check their two-tier cache inside the factory,
/// which turns such a re-run into a cheap L1 hit rather than a duplicate
/// provider sweep. A strict "exactly once" guarantee would need a short-lived
/// result cache in front of the dictionary, which the two-tier cache already
/// provides at this layer's callers.
/// </summary>
public class AsyncSingleFlight
{
    private readonly ConcurrentDictionary<string, Task<object?>> _inFlight = new(StringComparer.OrdinalIgnoreCase);

    public async Task<T?> ExecuteAsync<T>(string key, Func<Task<T?>> factory, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(factory);

        if (string.IsNullOrWhiteSpace(key))
        {
            // Nothing to deduplicate on — still honor the caller's token.
            return await factory().WaitAsync(ct).ConfigureAwait(false);
        }

        // Fast path: join an existing flight without allocating anything.
        if (_inFlight.TryGetValue(key, out Task<object?>? existingTask))
        {
            return await AwaitSharedAsync<T>(existingTask, ct).ConfigureAwait(false);
        }

        // HL-01: GetOrAdd settles the miss/race atomically — no while(true)
        // spin. Losing the race discards our TCS (one small allocation) and
        // joins the winner's task instead.
        var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<object?> sharedTask = _inFlight.GetOrAdd(key, tcs.Task);

        if (!ReferenceEquals(sharedTask, tcs.Task))
        {
            // Lost the race — the winner's flight is already registered.
            return await AwaitSharedAsync<T>(sharedTask, ct).ConfigureAwait(false);
        }

        // We own the entry. Pre-observe faults on the stored task so the case
        // where every caller abandons their wait (all via WaitAsync above) and
        // the factory subsequently throws never escalates to
        // UnobservedTaskException; live awaiters still receive it normally.
        _ = sharedTask.ContinueWith(
            static t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        // Leader: run the factory detached from every caller's token (SF-01),
        // then wait like any other participant — OUR ct ends OUR wait, not the
        // shared work.
        _ = CompleteAsync(key, sharedTask, tcs, factory);
        return await AwaitSharedAsync<T>(sharedTask, ct).ConfigureAwait(false);
    }

    private async Task CompleteAsync<T>(
        string key,
        Task<object?> backedTask,
        TaskCompletionSource<object?> tcs,
        Func<Task<T?>> factory)
    {
        try
        {
            tcs.TrySetResult(await factory().ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // The factory cancelled itself (its own internal timeout/token —
            // never a caller's). Surface cancellation to current waiters.
            tcs.TrySetCanceled();
        }
        catch (Exception ex)
        {
            tcs.TrySetException(ex);
        }
        finally
        {
            // Remove only while we still own the slot: a fresh leader that
            // replaced this generation must not have its flight evicted by our
            // teardown.
            _inFlight.TryRemove(new KeyValuePair<string, Task<object?>>(key, backedTask));
        }
    }

    private static async Task<T?> AwaitSharedAsync<T>(Task<object?> sharedTask, CancellationToken ct)
    {
        object? result = await sharedTask.WaitAsync(ct).ConfigureAwait(false);
        return (T?)result;
    }
}
