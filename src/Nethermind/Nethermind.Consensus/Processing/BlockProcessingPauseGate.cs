// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Consensus.Processing;

internal sealed class BlockProcessingPauseGate
{
    private readonly Lock _lock = new();
    private volatile TaskCompletionSource? _resumeSignal;

    public bool IsPaused => _resumeSignal is not null;

    public bool Pause()
    {
        lock (_lock)
        {
            if (_resumeSignal is not null) return false;
            _resumeSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return true;
        }
    }

    public bool Resume()
    {
        TaskCompletionSource? signal;
        lock (_lock)
        {
            signal = _resumeSignal;
            _resumeSignal = null;
        }

        signal?.TrySetResult();
        return signal is not null;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask WaitWhilePausedAsync(CancellationToken cancellationToken) =>
        _resumeSignal is null ? ValueTask.CompletedTask : WaitWhilePausedSlowAsync(cancellationToken);

    private async ValueTask WaitWhilePausedSlowAsync(CancellationToken cancellationToken)
    {
        while (_resumeSignal is { } resume)
        {
            await resume.Task.WaitAsync(cancellationToken);
        }
    }
}
