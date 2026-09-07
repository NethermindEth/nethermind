// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Threading;
using Nethermind.Int256;

namespace Nethermind.Merge.Plugin.Test;

/// <summary>
/// An improvement context that never improves: its task is already completed with the block it was given.
/// </summary>
/// <param name="cts">Cancelled by <see cref="CancelOngoingImprovements"/>; when omitted, cancellation is a no-op.</param>
/// <param name="onFirstDispose">Invoked the first time the context is disposed.</param>
/// <param name="onCancelling">Invoked before <paramref name="cts"/> is cancelled.</param>
internal sealed class MockBlockImprovementContext(
    Block currentBestBlock,
    DateTimeOffset startDateTime,
    SharedCancellationTokenSource? cts = null,
    Action? onFirstDispose = null,
    Action? onCancelling = null) : IBlockImprovementContext
{
    private volatile bool _disposed;
    private int _disposeCount;

    public Task<Block?> ImprovementTask { get; } = Task.FromResult((Block?)currentBestBlock);
    public Block? CurrentBestBlock { get; } = currentBestBlock;
    public UInt256 BlockFees { get; }
    public bool Disposed => _disposed;
    public DateTimeOffset StartDateTime { get; } = startDateTime;

    public void CancelOngoingImprovements()
    {
        onCancelling?.Invoke();
        cts?.CancelAndDispose();
    }

    public void Dispose()
    {
        _disposed = true;
        if (Interlocked.Increment(ref _disposeCount) == 1)
        {
            onFirstDispose?.Invoke();
        }
    }
}
