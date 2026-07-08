// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.TxPool;

namespace Nethermind.Consensus.Processing;

/// <summary>
/// Speculatively warms the block-processing state caches in the idle gap between blocks, using top-of-mempool
/// transactions executed against the current head's post-state. When the next block builds on that head under the same
/// fork, <see cref="IBlockCachePreWarmer"/> reuses the warmed entries, so the main execution loop starts against a warm
/// cache instead of cold storage. The speculative pass is bounded (per-sender transaction depth and a block gas budget)
/// and is cancelled the moment a real block enters processing, so it never competes with block validation.
/// </summary>
public sealed class MempoolStatePrewarmer : IDisposable
{
    private readonly ITxPool _txPool;
    private readonly IBlockTree _blockTree;
    private readonly ISpecProvider _specProvider;
    private readonly IBlockCachePreWarmer _preWarmer;
    private readonly ITimestamper _timestamper;
    private readonly ILogger _logger;
    private readonly int _maxTxPerSender;
    private readonly bool _enabled;

    // Monotonic head counter: a queued pass runs only while it still reflects the latest head, so a stale pass can never
    // clobber the warming started for a newer head.
    private long _generation;

    public MempoolStatePrewarmer(
        IBlockCachePreWarmer preWarmer,
        ITxPool txPool,
        IBlockTree blockTree,
        ISpecProvider specProvider,
        ITimestamper timestamper,
        IBlocksConfig blocksConfig,
        ILogManager logManager)
    {
        _preWarmer = preWarmer;
        _txPool = txPool;
        _blockTree = blockTree;
        _specProvider = specProvider;
        _timestamper = timestamper;
        _logger = logManager.GetClassLogger<MempoolStatePrewarmer>();
        _maxTxPerSender = Math.Max(1, blocksConfig.MempoolPreWarmMaxTxPerSender);
        _enabled = blocksConfig.PreWarmStateFromMempool;

        if (_enabled)
        {
            _blockTree.NewHeadBlock += OnNewHeadBlock;
            if (_logger.IsDebug) _logger.Debug($"Mempool state pre-warming enabled (max {_maxTxPerSender} txs/sender).");
        }
    }

    private void OnNewHeadBlock(object? sender, BlockEventArgs e)
    {
        Block head = e.Block;
        long generation = Interlocked.Increment(ref _generation);
        // Do the mempool selection off the block-tree notification thread so head updates are never delayed.
        ThreadPool.UnsafeQueueUserWorkItem(
            static state => state.self.PreWarmFromMempool(state.head, state.generation),
            (self: this, head, generation),
            preferLocal: false);
    }

    private void PreWarmFromMempool(Block head, long generation)
    {
        try
        {
            if (IsStale(generation)) return;

            BlockHeader headHeader = head.Header;
            NextBlockContext next = PrepareNextBlockContext(headHeader);

            Block speculativeBlock = BuildSpeculativeBlock(next);
            if (speculativeBlock.Transactions.Length == 0) return;

            if (IsStale(generation)) return;
            _preWarmer.StartSpeculativePreWarm(speculativeBlock, headHeader, next.Spec);
        }
        catch (Exception ex)
        {
            if (_logger.IsDebug) _logger.Debug($"Error starting mempool pre-warm for head {head.Number}: {ex}");
        }
    }

    private bool IsStale(long generation) => Volatile.Read(ref _generation) != generation;

    private NextBlockContext PrepareNextBlockContext(BlockHeader parent)
    {
        ulong number = parent.Number + 1;
        ulong timestamp = Math.Max(parent.Timestamp + 1, _timestamper.UnixTime.Seconds);
        IReleaseSpec spec = _specProvider.GetSpec(new ForkActivation(number, timestamp));
        UInt256 baseFee = BaseFeeCalculator.Calculate(parent, spec);

        BlockHeader header = new(
            parent.Hash!,
            Keccak.OfAnEmptySequenceRlp,
            parent.GasBeneficiary ?? Address.Zero,
            UInt256.Zero,
            number,
            parent.GasLimit,
            timestamp,
            [])
        {
            MixHash = parent.MixHash,
            BaseFeePerGas = baseFee,
            ParentBeaconBlockRoot = parent.ParentBeaconBlockRoot,
        };

        return new NextBlockContext(header, spec);
    }

    private Block BuildSpeculativeBlock(NextBlockContext next)
    {
        IDictionary<AddressAsKey, Transaction[]> bySender = _txPool.GetPendingTransactionsBySender(filterToReadyTx: true, next.Header.BaseFeePerGas);
        if (bySender.Count == 0) return EmptyBlock(next.Header);

        Transaction[] selected = SelectTransactions(bySender, _maxTxPerSender, next.Header.GasLimit);
        return new Block(next.Header, new BlockBody(selected, uncles: [], withdrawals: null));
    }

    /// <summary>
    /// Picks the transactions to warm: at most <paramref name="maxTxPerSender"/> per sender (in nonce order, as returned
    /// by the pool) and no more in total than <paramref name="gasBudget"/> worth of gas. Bounding both dimensions caps
    /// the work a flooded mempool can force during a speculative pass.
    /// </summary>
    internal static Transaction[] SelectTransactions(IDictionary<AddressAsKey, Transaction[]> bySender, int maxTxPerSender, ulong gasBudget)
    {
        ulong gasUsed = 0;
        List<Transaction> selected = new(Math.Min(bySender.Count * maxTxPerSender, 4096));

        foreach (KeyValuePair<AddressAsKey, Transaction[]> senderTxs in bySender)
        {
            Transaction[] txs = senderTxs.Value;
            int take = Math.Min(txs.Length, maxTxPerSender);
            for (int i = 0; i < take; i++)
            {
                Transaction tx = txs[i];
                if (gasUsed + tx.GasLimit > gasBudget) break;
                gasUsed += tx.GasLimit;
                selected.Add(tx);
            }

            if (gasUsed >= gasBudget) break;
        }

        return selected.ToArray();
    }

    private static Block EmptyBlock(BlockHeader header) => new(header, new BlockBody([], [], null));

    public void Dispose()
    {
        if (_enabled)
        {
            _blockTree.NewHeadBlock -= OnNewHeadBlock;
        }
        _preWarmer.CancelSpeculativePreWarm();
    }

    private readonly record struct NextBlockContext(BlockHeader Header, IReleaseSpec Spec);
}
