// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Consensus.Processing;
using Nethermind.Core;

namespace Nethermind.Merge.Plugin.Test.Synchronization;

public class EmptyBlockProcessingQueue : IBlockProcessingQueue
{
    public void Enqueue(Block block, ProcessingOptions processingOptions)
    {
        BlockRemoved?.Invoke(this, new BlockHashEventArgs(block.Hash!, ProcessingResult.Success));
        ProcessingQueueEmpty?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? ProcessingQueueEmpty;
    public event EventHandler<BlockHashEventArgs>? BlockRemoved;
    public int Count => 0;
    public ValueTask Emptied() => default;
}
