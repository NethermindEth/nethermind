// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Consensus.Processing;

/// <summary>
/// A reusable async gate for a single-consumer loop: the loop parks while paused and proceeds once
/// resumed. <see cref="Pause"/> and <see cref="Resume"/> may be called from any thread.
/// </summary>
internal sealed class BlockProcessingPauseGate
{
    private readonly Lock _lock = new();

    // Non-null exactly while paused; the task completes when processing is resumed.
    private volatile TaskCompletionSource? _resumeSignal;

    public bool IsPaused => _resumeSignal is not null;

    /// <returns><c>true</c> if this call transitioned from running to paused.</returns>
    public bool Pause()
    {
        lock (_lock)
        {
            if (_resumeSignal is not null) return false;
            _resumeSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return true;
        }
    }

    /// <returns><c>true</c> if this call transitioned from paused to running.</returns>
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

    /// <summary>Completes synchronously when not paused; otherwise awaits <see cref="Resume"/> or cancellation.</summary>
    public async ValueTask WaitWhilePausedAsync(CancellationToken cancellationToken)
    {
        while (_resumeSignal is { } resume)
        {
            await resume.Task.WaitAsync(cancellationToken);
        }
    }
}
