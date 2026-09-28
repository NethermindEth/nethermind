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
    private readonly Lazy<IBlockProcessingQueue> _processingQueue;
    private const int QueueNotSubscribed = 0;
    private const int QueueSubscribed = 1;
    // Disposed, or the queue could not be resolved; either way never subscribed again.
    private const int QueueClosed = 2;
    private int _queueSubscription;
    private readonly ISpecProvider _specProvider;
    private readonly IBlockCachePreWarmer _preWarmer;
    private readonly ITimestamper _timestamper;
    private readonly ILogger _logger;
    private readonly ulong _maxHeadAgeSeconds;
    private readonly bool _enabled;
    private int _disposed;

    // The newest session's token source, cancelled once a block is queued for processing: that block's processing scope
    // joins the session anyway, so it stops while the block is recovered and handed over, not when the scope opens.
    private readonly Lock _sessionLock = new();
    private CancellationTokenSource? _session;
    private long _sessionGeneration;

    // Monotonic: a queued pass runs only while it still reflects the latest head.
    private long _generation;
    private readonly ulong _secondsPerSlot;

    public MempoolStatePrewarmer(
        IBlockCachePreWarmer preWarmer,
        IBlockProducerTxSourceFactory txSourceFactory,
        IBlockTree blockTree,
        Lazy<IBlockProcessingQueue> processingQueue,
        ISpecProvider specProvider,
        ITimestamper timestamper,
        IBlocksConfig blocksConfig,
        ILogManager logManager)
    {
        _preWarmer = preWarmer;
        _txSource = new Lazy<ITxSource>(txSourceFactory.Create, LazyThreadSafetyMode.PublicationOnly);
        _blockTree = blockTree;
        _processingQueue = processingQueue;
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

    // Resolved on the first head rather than at construction: the queue's processor depends on the prewarmer this is
    // activated with. Under the session lock, so dispose cannot run between the check and the subscription.
    private void SubscribeToProcessingQueue()
    {
        if (Volatile.Read(ref _queueSubscription) != QueueNotSubscribed) return;
        using (_sessionLock.EnterScope())
        {
            if (_queueSubscription != QueueNotSubscribed) return;
            try
            {
                IBlockProcessingQueue queue = _processingQueue.Value;
                queue.BlockAdded += OnBlockQueued;
                queue.BlockRemoved += OnBlockRemoved;
                _queueSubscription = QueueSubscribed;
            }
            catch (Exception ex)
            {
                // Best effort, and the lazy caches a failed resolution: stay unsubscribed, so sessions are joined when a
                // processing scope opens, rather than throw into every head notification.
                _queueSubscription = QueueClosed;
                if (_logger.IsDebug) _logger.Debug($"Mempool pre-warming could not subscribe to the processing queue: {ex}");
            }
        }
    }

    private void OnBlockQueued(object? sender, BlockEventArgs e)
    {
        CancellationTokenSource? session;
        using (_sessionLock.EnterScope()) session = _session;
        session?.Cancel();
    }

    // A queued block can leave without success, some before ever reaching a processing scope, and then no new head
    // follows to start another session; warm again from the unchanged head.
    private void OnBlockRemoved(object? sender, BlockRemovedEventArgs e)
    {
        // Both were processed and committed; a new head, if any, starts the next session.
        if (e.ProcessingResult is ProcessingResult.Success or ProcessingResult.InclusionListUnsatisfied) return;

        // Read before the head: a head published after this read must win, so the restart claims the next generation
        // only if none has been taken since, and a newer head's session is never displaced by the old head's.
        long generation = Volatile.Read(ref _generation);
        if (_blockTree.Head is not Block head || IsTooOld(head)) return;
        if (Interlocked.CompareExchange(ref _generation, generation + 1, generation) == generation) ScheduleWarm(head, generation + 1);
    }

    private void OnNewHeadBlock(object? sender, BlockEventArgs e)
    {
        Block head = e.Block;

        // Skip while catching up: a stale head means there is no idle gap to fill.
        if (IsTooOld(head)) return;

        SubscribeToProcessingQueue();
        ScheduleWarm(head, Interlocked.Increment(ref _generation));
    }

    private bool IsTooOld(Block head) => head.Header.Timestamp + _maxHeadAgeSeconds < _timestamper.UnixTime.Seconds;

    // Queued off the notification thread so head updates are never delayed.
    private void ScheduleWarm(Block head, long generation) =>
        ThreadPool.UnsafeQueueUserWorkItem(
            static state => state.self.PreWarmFromMempool(state.head, state.generation),
            (self: this, head, generation),
            preferLocal: false);

    private void PreWarmFromMempool(Block head, long generation)
    {
        try
        {
            if (IsStale(generation)) return;

            BlockHeader headHeader = head.Header;
            NextBlockContext next = PrepareNextBlockContext(headHeader);

            Dictionary<AddressAsKey, int> warmedPerSender = [];
            Dictionary<AddressAsKey, SenderSelection> selectedBySender = [];

            // Not linked or disposed: it only carries this session's cancellation, and holds no registration or timer.
            CancellationTokenSource session = new();
            using (_sessionLock.EnterScope())
            {
                if (Volatile.Read(ref _disposed) != 0) session.Cancel();
                // A pass for an older head that ran late must not displace the newer head's session.
                else if (generation > _sessionGeneration)
                {
                    _sessionGeneration = generation;
                    _session = session;
                }
            }

            _preWarmer.StartSpeculativePreWarm(
                headHeader,
                next.Spec,
                generation,
                token => (token.IsCancellationRequested || IsStale(generation)) ? null : BuildDeltaBlock(headHeader, warmedPerSender, selectedBySender, token),
                IdlePassDelayMs,
                session.Token);
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
    private (Block Block, IReleaseSpec Spec)? BuildDeltaBlock(BlockHeader parent, Dictionary<AddressAsKey, int> warmedPerSender,
        Dictionary<AddressAsKey, SenderSelection> selectedBySender, CancellationToken token)
    {
        NextBlockContext next = PrepareNextBlockContext(parent);
        Transaction[]? delta = SelectDelta(_txSource.Value.GetTransactions(parent, next.Header, next.Header.GasLimit), warmedPerSender, selectedBySender, token);
        return delta is null ? null : (new Block(next.Header, new BlockBody(delta, uncles: [], withdrawals: null)), next.Spec);
    }

    /// <summary>
    /// Picks the transactions to warm this pass from the producer's ordered/filtered selection, skipping senders whose
    /// selected txs are all already warmed (tracked in <paramref name="warmedPerSender"/>); a sender with new txs has its
    /// full set replayed so later-nonce txs see their predecessors' state.
    /// </summary>
    /// <remarks>The optional scratch dictionary is owned by one speculative session and cleared between passes.</remarks>
    /// <returns>The delta, or <see langword="null"/> when <paramref name="token"/> ends the pass before selection completes;
    /// nothing is then recorded as warmed.</returns>
    internal static Transaction[]? SelectDelta(IEnumerable<Transaction> orderedTxs, Dictionary<AddressAsKey, int> warmedPerSender,
        Dictionary<AddressAsKey, SenderSelection>? bySender = null, CancellationToken token = default)
    {
        bySender ??= [];
        bySender.Clear();
        using ArrayPoolListRef<(Transaction tx, int next)> transactions = new(orderedTxs is ICollection<Transaction> collection ? collection.Count : 0);
        foreach (Transaction tx in orderedTxs)
        {
            // Each pull runs the producer's ordering and filters; a block arriving must not wait for the whole selection.
            if (token.IsCancellationRequested)
            {
                bySender.Clear();
                return null;
            }

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

        CancellationTokenSource? session;
        using (_sessionLock.EnterScope())
        {
            if (_queueSubscription == QueueSubscribed)
            {
                IBlockProcessingQueue queue = _processingQueue.Value;
                queue.BlockAdded -= OnBlockQueued;
                queue.BlockRemoved -= OnBlockRemoved;
            }
            _queueSubscription = QueueClosed;
            session = _session;
            _session = null;
        }
        session?.Cancel();
    }

    internal struct SenderSelection
    {
        public int First;
        public int Last;
        public int Count;
    }

    private readonly record struct NextBlockContext(BlockHeader Header, IReleaseSpec Spec);
}
