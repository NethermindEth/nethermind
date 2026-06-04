// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
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
}
