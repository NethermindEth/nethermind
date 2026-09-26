// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
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
    // A block is expected within the first third of its slot; past that, bet on the next one.
    private const ulong ArrivalGraceSlotFraction = 3;

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
            Dictionary<AddressAsKey, SenderSelection> selectedBySender = [];

            _preWarmer.StartSpeculativePreWarm(
                headHeader,
                next.Spec,
                generation,
                token => (token.IsCancellationRequested || IsStale(generation)) ? null : BuildDeltaBlock(headHeader, warmedPerSender, selectedBySender),
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
    /// The next block's timestamp is the first slot boundary after the parent that can still produce the next block,
    /// so the EIP-4788 ring-buffer slots the system call touches, indexed by timestamp, are the ones being warmed.
    /// </summary>
    /// <remarks>
    /// A boundary that has just passed is still the likeliest next block: the block for it is proposed at the boundary
    /// and reaches the execution layer a second or more later. Moving on the instant it passes warms the slot after it
    /// for the last seconds of every gap, and never warms the right one at all when the head itself arrived late
    /// enough that the first boundary is already behind us. The grace is how long a passed boundary stays the bet;
    /// after it, a missed slot is the better explanation.
    /// </remarks>
    internal static ulong PredictNextTimestamp(ulong parentTimestamp, ulong now, ulong secondsPerSlot)
    {
        ulong grace = secondsPerSlot / ArrivalGraceSlotFraction;
        ulong elapsed = now > parentTimestamp + grace ? now - parentTimestamp - grace : 0;
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
    /// rather than leaving the whole gap warming the cells of a block that never arrived. Its spec travels with it, so a
    /// fork activating inside the gap warms under the spec the predicted block would run rather than the session's.
    /// </remarks>
    private (Block Block, IReleaseSpec Spec) BuildDeltaBlock(BlockHeader parent, Dictionary<AddressAsKey, int> warmedPerSender,
        Dictionary<AddressAsKey, SenderSelection> selectedBySender)
    {
        NextBlockContext next = PrepareNextBlockContext(parent);
        Transaction[] delta = SelectDelta(_txSource.Value.GetTransactions(parent, next.Header, next.Header.GasLimit), warmedPerSender, selectedBySender);
        return (new Block(next.Header, new BlockBody(delta, uncles: [], withdrawals: null)), next.Spec);
    }

    /// <summary>
    /// Picks the transactions to warm this pass from the producer's ordered/filtered selection, skipping senders whose
    /// selected txs are all already warmed (tracked in <paramref name="warmedPerSender"/>); a sender with new txs has its
    /// full set replayed so later-nonce txs see their predecessors' state.
    /// </summary>
    /// <remarks>The optional scratch dictionary is owned by one speculative session and cleared between passes.</remarks>
    internal static Transaction[] SelectDelta(IEnumerable<Transaction> orderedTxs, Dictionary<AddressAsKey, int> warmedPerSender,
        Dictionary<AddressAsKey, SenderSelection>? bySender = null)
    {
        bySender ??= [];
        bySender.Clear();
        using ArrayPoolListRef<(Transaction tx, int next)> transactions = new(orderedTxs is ICollection<Transaction> collection ? collection.Count : 0);
        foreach (Transaction tx in orderedTxs)
        {
            if (tx.SenderAddress is not Address sender) continue;
            ref SenderSelection group = ref CollectionsMarshal.GetValueRefOrAddDefault(bySender, sender, out bool exists);
            int index = transactions.Count;
            if (exists) transactions.GetRef(group.Last).next = index;
            else group.First = index;
            group.Last = index;
            group.Count++;
            transactions.Add((tx, -1));
        }

        return SelectGroupedDelta(transactions.AsSpan(), bySender, warmedPerSender);
    }

    /// <remarks>Kept out of line to reduce the stack frame of the transaction-grouping loop.</remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Transaction[] SelectGroupedDelta(ReadOnlySpan<(Transaction tx, int next)> transactions,
        Dictionary<AddressAsKey, SenderSelection> bySender, Dictionary<AddressAsKey, int> warmedPerSender)
    {
        if (warmedPerSender.Count == 0)
        {
            Transaction[] initialDelta = SelectInitialDelta(transactions, bySender, warmedPerSender);
            bySender.Clear();
            return initialDelta;
        }
        int deltaCount = 0;
        foreach (KeyValuePair<AddressAsKey, SenderSelection> senderGroup in bySender)
        {
            warmedPerSender.TryGetValue(senderGroup.Key, out int warmed);
            if (senderGroup.Value.Count > warmed) deltaCount += senderGroup.Value.Count;
        }
        if (deltaCount == 0)
        {
            bySender.Clear();
            return [];
        }
        Transaction[] delta = new Transaction[deltaCount];
        int position = 0;
        foreach (KeyValuePair<AddressAsKey, SenderSelection> senderGroup in bySender)
        {
            ref int warmed = ref CollectionsMarshal.GetValueRefOrAddDefault(warmedPerSender, senderGroup.Key, out _);
            if (senderGroup.Value.Count <= warmed) continue;
            for (int index = senderGroup.Value.First; index >= 0; index = transactions[index].next)
                delta[position++] = transactions[index].tx;
            warmed = senderGroup.Value.Count;
        }

        Debug.Assert(position == deltaCount, "Sizing and filling must select the same number of transactions.");
        bySender.Clear();
        return delta;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Transaction[] SelectInitialDelta(ReadOnlySpan<(Transaction tx, int next)> transactions,
        Dictionary<AddressAsKey, SenderSelection> bySender, Dictionary<AddressAsKey, int> warmedPerSender)
    {
        // A fresh pass selects every group, so the final array size is already known.
        warmedPerSender.EnsureCapacity(bySender.Count);
        Transaction[] delta = transactions.Length == 0 ? [] : new Transaction[transactions.Length];
        int position = 0;
        foreach (KeyValuePair<AddressAsKey, SenderSelection> senderGroup in bySender)
        {
            for (int index = senderGroup.Value.First; index >= 0; index = transactions[index].next)
                delta[position++] = transactions[index].tx;
            warmedPerSender.Add(senderGroup.Key, senderGroup.Value.Count);
        }
        return delta;
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

    internal struct SenderSelection
    {
        public int First;
        public int Last;
        public int Count;
    }

    private readonly record struct NextBlockContext(BlockHeader Header, IReleaseSpec Spec);
}
