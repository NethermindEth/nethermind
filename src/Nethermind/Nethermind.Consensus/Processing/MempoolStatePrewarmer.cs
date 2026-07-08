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
/// transactions executed against the current head's post-state. It runs repeated delta passes across the slot so
/// transactions arriving late still get warmed, deduping per sender so an already-warmed sender is only re-warmed when
/// new (or higher-nonce) transactions appear for it. When the next block builds on that head under the same fork,
/// <see cref="IBlockCachePreWarmer"/> reuses the warmed entries, so the main execution loop starts against a warm cache
/// instead of cold storage. Warming is bounded (per-sender transaction depth and a block gas budget per pass) and is
/// cancelled the moment a real block enters processing, so it never competes with block validation.
/// </summary>
public sealed class MempoolStatePrewarmer : IDisposable
{
    // How long a delta pass waits before re-sampling the mempool when it found nothing new to warm. Small enough to
    // catch transactions arriving late in the slot, large enough not to busy-spin.
    private const int IdlePassDelayMs = 100;

    private readonly ITxPool _txPool;
    private readonly IBlockTree _blockTree;
    private readonly ISpecProvider _specProvider;
    private readonly IBlockCachePreWarmer _preWarmer;
    private readonly ITimestamper _timestamper;
    private readonly ILogger _logger;
    private readonly int _maxTxPerSender;
    private readonly ulong _maxHeadAgeSeconds;
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
        // Warm only when the head is recent (a few slots of wall-clock). During sync the head advances rapidly over
        // historical blocks whose timestamps are far in the past — there is no idle gap to fill, so we skip entirely
        // and never contend with block import for the txpool lock or the caches.
        _maxHeadAgeSeconds = Math.Max(1UL, blocksConfig.SecondsPerSlot) * 4;
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

        // Skip while syncing/catching up: a stale head means there is no pre-block idle gap to fill, and warming would
        // only steal the txpool lock and CPU from block import.
        if (head.Header.Timestamp + _maxHeadAgeSeconds < _timestamper.UnixTime.Seconds) return;

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

            // Per-session dedup state: how many transactions we have already warmed for each sender. Accessed only by
            // the speculative loop thread (the delta source below is invoked serially), so no synchronization is needed.
            Dictionary<AddressAsKey, int> warmedPerSender = [];

            _preWarmer.StartSpeculativePreWarm(
                headHeader,
                next.Spec,
                token => (token.IsCancellationRequested || IsStale(generation)) ? null : BuildDeltaBlock(next, warmedPerSender),
                IdlePassDelayMs);
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

    private Block? BuildDeltaBlock(NextBlockContext next, Dictionary<AddressAsKey, int> warmedPerSender)
    {
        IDictionary<AddressAsKey, Transaction[]> bySender = _txPool.GetPendingTransactionsBySender(filterToReadyTx: true, next.Header.BaseFeePerGas);
        if (bySender.Count == 0) return null;

        Transaction[] delta = SelectDirtySenders(bySender, warmedPerSender, _maxTxPerSender, next.Header.GasLimit);
        return delta.Length == 0 ? null : new Block(next.Header, new BlockBody(delta, uncles: [], withdrawals: null));
    }

    /// <summary>
    /// Picks the transactions to warm on this delta pass, deduping per sender. A sender is skipped when its whole in-cap
    /// queue (at most <paramref name="maxTxPerSender"/>, in nonce order) is already warmed; otherwise its full in-cap
    /// queue is replayed — the already-warmed prefix is cheap (its reads hit the warm cache) and it gives the new
    /// later-nonce transactions their predecessors' nonce/balance so they actually warm rather than failing a nonce
    /// check. Total selected gas is capped at <paramref name="gasBudget"/>. <paramref name="warmedPerSender"/> is updated
    /// in place with how far each sender has now been warmed.
    /// </summary>
    internal static Transaction[] SelectDirtySenders(IDictionary<AddressAsKey, Transaction[]> bySender, Dictionary<AddressAsKey, int> warmedPerSender, int maxTxPerSender, ulong gasBudget)
    {
        ulong gasUsed = 0;
        List<Transaction> selected = new(Math.Min(bySender.Count * maxTxPerSender, 4096));

        foreach (KeyValuePair<AddressAsKey, Transaction[]> senderTxs in bySender)
        {
            Transaction[] txs = senderTxs.Value;
            int take = Math.Min(txs.Length, maxTxPerSender);
            int alreadyWarmed = warmedPerSender.TryGetValue(senderTxs.Key, out int warmed) ? warmed : 0;
            if (take <= alreadyWarmed) continue; // nothing new within the cap for this sender

            int added = 0;
            for (int i = 0; i < take; i++)
            {
                if (gasUsed + txs[i].GasLimit > gasBudget) break;
                gasUsed += txs[i].GasLimit;
                selected.Add(txs[i]);
                added++;
            }

            if (added > 0) warmedPerSender[senderTxs.Key] = added;
            if (gasUsed >= gasBudget) break;
        }

        return selected.ToArray();
    }

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
