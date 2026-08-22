using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace Octave.Core.Helpers;

public class AsyncSingleFlight
{
    private readonly ConcurrentDictionary<string, Task<object?>> _inFlight = new(StringComparer.OrdinalIgnoreCase);

    public async Task<T?> ExecuteAsync<T>(string key, Func<Task<T?>> factory)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return await factory().ConfigureAwait(false);
        }

        while (true)
        {
            if (_inFlight.TryGetValue(key, out var existingTask))
            {
                var result = await existingTask.ConfigureAwait(false);
                return (T?)result;
            }

            var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_inFlight.TryAdd(key, tcs.Task))
            {
                try
                {
                    var res = await factory().ConfigureAwait(false);
                    tcs.TrySetResult(res);
                    return res;
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                    throw;
                }
                finally
                {
                    _inFlight.TryRemove(key, out _);
                }
            }
        }
    }
}
