// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Core.Events;

namespace Nethermind.Consensus.Processing;

public static class BlockProcessingQueueExtensions
{
    public static async Task WaitForBlockProcessing(this IBlockProcessingQueue blockProcessingQueue, CancellationToken cancellationToken = default)
    {
        if (!blockProcessingQueue.IsEmpty)
        {
            await Wait.ForEvent(cancellationToken,
                e =>
                {
                    blockProcessingQueue.ProcessingQueueEmpty += e;
                    // The queue may have drained between the initial check and subscribing.
                    if (blockProcessingQueue.IsEmpty) e(blockProcessingQueue, EventArgs.Empty);
                },
                e => blockProcessingQueue.ProcessingQueueEmpty -= e);
        }
    }

    /// <summary>
    /// Waits, for at most <paramref name="bound"/>, for the copy of the block that has had its verdict to leave the
    /// queue, which is when a block answered VALID is committed and readable through the chain.
    /// </summary>
    /// <returns><c>false</c> when that copy was still in the queue at the bound; <c>true</c> otherwise, at once when
    /// no copy of the block has had its verdict.</returns>
    public static async ValueTask<bool> WaitForExecutedCopyAsync(this IBlockProcessingQueue queue, Hash256 blockHash, TimeSpan bound)
    {
        ValueTask removed = queue.WaitUntilExecutedCopyRemovedAsync(blockHash);
        if (removed.IsCompleted) return true;

        Task committed = removed.AsTask();
        using CancellationTokenSource timer = new();
        if (await Task.WhenAny(committed, Task.Delay(bound, timer.Token)) != committed) return false;

        timer.Cancel();
        return true;
    }
}
