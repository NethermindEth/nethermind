using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Threading;

namespace Nethermind.Core.Tasks;

public class AsyncManualResetEventSlim
{
    private volatile TaskCompletionSource<bool> _tcs = CreateNewTcs();

    private static TaskCompletionSource<bool> CreateNewTcs() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public AsyncManualResetEventSlim(bool initialState = false)
    {
        if (initialState)
        {
            _tcs.TrySetResult(true);
        }
    }

    public bool IsSet => _tcs.Task.IsCompleted;

    public void Set() => _tcs.TrySetResult(true);

    public void Reset()
    {
        TaskCompletionSource<bool>? newTcs = null;
        while (true)
        {
            TaskCompletionSource<bool> tcs = _tcs;
            if (!tcs.Task.IsCompleted)
                return;

            newTcs ??= CreateNewTcs();
            if (Interlocked.CompareExchange(ref _tcs, newTcs, tcs) == tcs)
                return;
        }
    }

    public Task WaitAsync() => _tcs.Task;

    public ValueTask WaitValueAsync() =>
        _tcs.Task.IsCompleted
            ? ValueTask.CompletedTask
            : new ValueTask(_tcs.Task);

    public ValueTask WaitValueAsync(CancellationToken cancellationToken) =>
        _tcs.Task.IsCompleted
            ? ValueTask.CompletedTask
            : new ValueTask(_tcs.Task.WaitAsync(cancellationToken));

    public ValueTask<bool> WaitValueAsync(TimeSpan timeout, CancellationToken cancellationToken = default) =>
        _tcs.Task.IsCompleted
            ? ValueTask.FromResult(true)
            : new ValueTask<bool>(_tcs.Task.WaitAsync(timeout, cancellationToken));
}
