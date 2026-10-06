// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Consensus.Processing;

/// <summary>The blocks waiting in the processing queue with their senders recovered, by parent.</summary>
/// <remarks>
/// The queue publishes a block once its senders are recovered and withdraws it when processing takes it, so what is
/// here is what processing reaches next. The prewarmer reads it to run those blocks ahead.
/// </remarks>
public sealed class ProcessingLookAhead
{
    // Far above the queue's own bound; only a block the queue never withdrew could fill it.
    private const int MaxBlocks = 4 * BlockchainProcessor.MaxProcessingQueueSize;

    private readonly ConcurrentDictionary<Hash256, Block> _byParent = new();

    public int Count => _byParent.Count;

    public void Publish(Block block)
    {
        if (block.ParentHash is not Hash256 parentHash || block.IsGenesis) return;
        if (_byParent.Count >= MaxBlocks) _byParent.Clear();
        _byParent[parentHash] = block;
    }

    public void Withdraw(Block block)
    {
        if (block.ParentHash is Hash256 parentHash) _byParent.TryRemove(new(parentHash, block));
    }

    /// <summary>The queued child of <paramref name="parent"/>, if any.</summary>
    public Block? FindChild(Block parent) =>
        parent.Hash is Hash256 hash && _byParent.TryGetValue(hash, out Block? child) ? child : null;
}
