// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
                e => blockProcessingQueue.ProcessingQueueEmpty += e,
                e => blockProcessingQueue.ProcessingQueueEmpty -= e);
        }
    }

    public static async Task<ProcessingResult> WaitForBlockProcessing(this IBlockProcessingQueue blockProcessingQueue, Hash256 blockHash, CancellationToken cancellationToken = default)
    {
        var res = await Wait.ForEventCondition<BlockRemovedEventArgs>(cancellationToken,
            e => blockProcessingQueue.BlockRemoved += e,
            e => blockProcessingQueue.BlockRemoved -= e,
            e => e.BlockHash == blockHash).ConfigureAwait(false);

        return res.ProcessingResult;
    }
}
