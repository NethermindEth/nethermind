// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Specs;
using Nethermind.Logging;

namespace Nethermind.Consensus.Processing;

/// <summary>
/// Speculatively warms the state caches in the idle gap between blocks, selecting the txs most likely to be in the next
/// block via the producer's <see cref="ITxSource"/> and re-sampling across the slot. The warmed caches are reused when
/// the next block builds on that head; warming is cancelled the moment a real block enters processing.
/// </summary>
public sealed class MempoolStatePrewarmer : IDisposable
{
    private const int IdlePassDelayMs = 100;

    private readonly Lazy<ITxSource> _txSource;
    private readonly IBlockTree _blockTree;
    private readonly ISpecProvider _specProvider;
    private readonly IBlockCachePreWarmer _preWarmer;
    private readonly ITimestamper _timestamper;
    private readonly ILogger _logger;
    private readonly ulong _maxHeadAgeSeconds;
    private readonly bool _enabled;
    private readonly CancellationTokenSource _cts = new();
    private int _disposed;

    // Monotonic: a queued pass runs only while it still reflects the latest head.
    private long _generation;
    private readonly ulong _secondsPerSlot;

    public MempoolStatePrewarmer(
        IBlockCachePreWarmer preWarmer,
        IBlockProducerTxSourceFactory txSourceFactory,
        IBlockTree blockTree,
        ISpecProvider specProvider,
        ITimestamper timestamper,
        IBlocksConfig blocksConfig,
        ILogManager logManager)
    {
        _preWarmer = preWarmer;
        _txSource = new Lazy<ITxSource>(txSourceFactory.Create, LazyThreadSafetyMode.PublicationOnly);
        _blockTree = blockTree;
        _specProvider = specProvider;
        _timestamper = timestamper;
        _logger = logManager.GetClassLogger<MempoolStatePrewarmer>();
        _secondsPerSlot = Math.Max(1UL, blocksConfig.SecondsPerSlot);
        _maxHeadAgeSeconds = _secondsPerSlot * 4;
        _enabled = blocksConfig.PreWarming == PreWarmMode.BlockAndMempool;

        if (_enabled)
        {
            _blockTree.NewHeadBlock += OnNewHeadBlock;
            if (_logger.IsDebug) _logger.Debug("Mempool state pre-warming enabled.");
        }
    }

    private void OnNewHeadBlock(object? sender, BlockEventArgs e)
    {
        Block head = e.Block;

        // Skip while catching up: a stale head means there is no idle gap to fill.
        if (head.Header.Timestamp + _maxHeadAgeSeconds < _timestamper.UnixTime.Seconds) return;

        long generation = Interlocked.Increment(ref _generation);
        // Queue off the notification thread so head updates are never delayed.
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

            Dictionary<AddressAsKey, int> warmedPerSender = [];

            _preWarmer.StartSpeculativePreWarm(
                headHeader,
                next.Spec,
                generation,
                token => (token.IsCancellationRequested || IsStale(generation)) ? null : BuildDeltaBlock(headHeader, warmedPerSender),
                IdlePassDelayMs,
                _cts.Token);
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
        ulong timestamp = PredictNextTimestamp(parent.Timestamp, _timestamper.UnixTime.Seconds, _secondsPerSlot);
        IReleaseSpec spec = _specProvider.GetSpec(new ForkActivation(number, timestamp));

        return new NextBlockContext(BuildNextBlockHeader(parent, timestamp, spec), spec);
    }

    /// <summary>
    /// The next block's timestamp is the first slot boundary after the parent that has not passed yet, so the
    /// EIP-4788 ring-buffer slots the system call touches, indexed by timestamp, are the ones being warmed.
    /// </summary>
    internal static ulong PredictNextTimestamp(ulong parentTimestamp, ulong now, ulong secondsPerSlot)
    {
        ulong elapsed = now > parentTimestamp ? now - parentTimestamp : 0;
        ulong slots = Math.Max(1UL, (elapsed + secondsPerSlot - 1) / secondsPerSlot);
        return parentTimestamp + slots * secondsPerSlot;
    }

    /// <summary>
    /// Builds the synthetic "next block" header for warming.
    /// </summary>
    private static BlockHeader BuildNextBlockHeader(BlockHeader parent, ulong timestamp, IReleaseSpec spec)
    {
        BlockHeader header = parent.CreateSimulatedChild(timestamp);
        // Resolve the actual coinbase: on Clique, Beneficiary is a vote target, not the sealer.
        header.Beneficiary = parent.GasBeneficiary ?? Address.Zero;
        header.MixHash = parent.MixHash;
        header.BaseFeePerGas = BaseFeeCalculator.Calculate(parent, spec);
        header.ParentBeaconBlockRoot = parent.ParentBeaconBlockRoot;

        return header;
    }

    /// <remarks>
    /// The prediction is redone every pass so a missed slot moves the warm onto the slot that will actually land,
    /// rather than leaving the whole gap warming the cells of a block that never arrived.
    /// </remarks>
    private Block BuildDeltaBlock(BlockHeader parent, Dictionary<AddressAsKey, int> warmedPerSender)
    {
        NextBlockContext next = PrepareNextBlockContext(parent);
        Transaction[] delta = SelectDelta(_txSource.Value.GetTransactions(parent, next.Header, next.Header.GasLimit), warmedPerSender);
        return new Block(next.Header, new BlockBody(delta, uncles: [], withdrawals: null));
    }

    /// <summary>
    /// Picks the transactions to warm this pass from the producer's ordered/filtered selection, skipping senders whose
    /// selected txs are all already warmed (tracked in <paramref name="warmedPerSender"/>); a sender with new txs has its
    /// full set replayed so later-nonce txs see their predecessors' state.
    /// </summary>
    internal static Transaction[] SelectDelta(IEnumerable<Transaction> orderedTxs, Dictionary<AddressAsKey, int> warmedPerSender)
    {
        Dictionary<AddressAsKey, List<Transaction>> bySender = [];
        int total = 0;
        foreach (Transaction tx in orderedTxs)
        {
            if (tx.SenderAddress is not Address sender) continue;
            ref List<Transaction>? group = ref CollectionsMarshal.GetValueRefOrAddDefault(bySender, sender, out _);
            (group ??= new(4)).Add(tx);
            total++;
        }

        using ArrayPoolListRef<Transaction> delta = new(total);
        foreach (KeyValuePair<AddressAsKey, List<Transaction>> senderGroup in bySender)
        {
            ref int warmed = ref CollectionsMarshal.GetValueRefOrAddDefault(warmedPerSender, senderGroup.Key, out _);
            if (senderGroup.Value.Count <= warmed) continue;
            delta.AddRange(senderGroup.Value);
            warmed = senderGroup.Value.Count;
        }

        return delta.ToArray();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_enabled)
        {
            _blockTree.NewHeadBlock -= OnNewHeadBlock;
        }
        _cts.Cancel();
        _cts.Dispose();
    }

    private readonly record struct NextBlockContext(BlockHeader Header, IReleaseSpec Spec);
}
