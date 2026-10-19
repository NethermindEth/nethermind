// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Consensus.ProofAggregation;
using Nethermind.Core;
using Nethermind.Crypto;
using Nethermind.TxPool;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V73;

/// <summary>Records the inclusion and pool eviction of dependency-bearing transactions in an enabled
/// <see cref="ProofWrapperEventFeed"/>, until disposed.</summary>
public sealed class ProofWrapperChainEvents : IDisposable
{
    private readonly IBlockTree _blockTree;
    private readonly ITxPool _txPool;

    public ProofWrapperChainEvents(ProofWrapperEventFeed feed, IBlockTree blockTree, ITxPool txPool)
    {
        Feed = feed;
        _blockTree = blockTree;
        _txPool = txPool;
        blockTree.BlockAddedToMain += OnBlockAddedToMain;
        txPool.EvictedPending += OnEvictedPending;
    }

    public ProofWrapperEventFeed Feed { get; }

    private void OnBlockAddedToMain(object? sender, BlockReplacementEventArgs e)
    {
        foreach (Transaction transaction in e.Block.Transactions)
        {
            if (!Eip8288Dependencies.IsDependencyBearing(transaction)) continue;
            Feed.Record(new()
            {
                Type = ProofWrapperEventTypes.Included,
                Transactions = [transaction.Hash ?? transaction.CalculateHash()],
                Block = (long)e.Block.Number,
                BlockHash = e.Block.Hash
            });
        }
    }

    private void OnEvictedPending(object? sender, TxEventArgs e)
    {
        if (!Eip8288Dependencies.IsDependencyBearing(e.Transaction)) return;
        Feed.Record(new() { Type = ProofWrapperEventTypes.PoolEvicted, Transactions = [e.Transaction.Hash!], Detail = "evicted by pool policy" });
    }

    public void Dispose()
    {
        _blockTree.BlockAddedToMain -= OnBlockAddedToMain;
        _txPool.EvictedPending -= OnEvictedPending;
    }
}
