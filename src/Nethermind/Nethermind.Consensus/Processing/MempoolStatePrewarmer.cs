// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.Consensus.Processing;

/// <summary>
/// Speculatively warms the block-processing state caches in the idle gap between blocks. It selects transactions with
/// the block producer's own <see cref="ITxSource"/> (effective-gas-price ordered, gas-limited, fully filtered), so it
/// warms the transactions most likely to be in the next block, and runs repeated delta passes across the slot to catch
/// late arrivals. When the next block builds on that head under the same fork, <see cref="IBlockCachePreWarmer"/> reuses
/// the warmed caches instead of clearing them. Warming is cancelled the moment a real block enters processing, so it
/// never competes with block validation.
/// </summary>
public sealed class MempoolStatePrewarmer : IDisposable
{
    // How long a delta pass waits before re-sampling when it found nothing new to warm: small enough to catch
    // transactions arriving late in the slot, large enough not to busy-spin.
    private const int IdlePassDelayMs = 100;

    private readonly ITxSource _txSource;
    private readonly IBlockTree _blockTree;
    private readonly ISpecProvider _specProvider;
    private readonly IBlockCachePreWarmer _preWarmer;
    private readonly ITimestamper _timestamper;
    private readonly ILogger _logger;
    private readonly ulong _maxHeadAgeSeconds;
    private readonly bool _enabled;
    // Cancels the in-flight speculative session on dispose; linked into the prewarmer's own session token.
    private readonly CancellationTokenSource _cts = new();

    // Monotonic head counter so a queued pass runs only while it still reflects the latest head.
    private long _generation;

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
        _txSource = txSourceFactory.Create();
        _blockTree = blockTree;
        _specProvider = specProvider;
        _timestamper = timestamper;
        _logger = logManager.GetClassLogger<MempoolStatePrewarmer>();
        _maxHeadAgeSeconds = Math.Max(1UL, blocksConfig.SecondsPerSlot) * 4;
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

        // Skip while syncing/catching up: a stale head means there is no pre-block idle gap to fill.
        if (head.Header.Timestamp + _maxHeadAgeSeconds < _timestamper.UnixTime.Seconds) return;

        long generation = Interlocked.Increment(ref _generation);
        // Select and warm off the block-tree notification thread so head updates are never delayed.
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

            // Per-session dedup: how far each sender is already warmed. Only the speculative loop thread touches it
            // (the delta source below is invoked serially), so no synchronization is needed.
            Dictionary<AddressAsKey, int> warmedPerSender = [];

            _preWarmer.StartSpeculativePreWarm(
                headHeader,
                next.Spec,
                generation,
                token => (token.IsCancellationRequested || IsStale(generation)) ? null : BuildDeltaBlock(headHeader, next, warmedPerSender),
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
        ulong timestamp = Math.Max(parent.Timestamp + 1, _timestamper.UnixTime.Seconds);
        IReleaseSpec spec = _specProvider.GetSpec(new ForkActivation(number, timestamp));

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
            BaseFeePerGas = BaseFeeCalculator.Calculate(parent, spec),
            ParentBeaconBlockRoot = parent.ParentBeaconBlockRoot,
        };

        return new NextBlockContext(header, spec);
    }

    private Block? BuildDeltaBlock(BlockHeader parent, NextBlockContext next, Dictionary<AddressAsKey, int> warmedPerSender)
    {
        Transaction[] delta = SelectDelta(_txSource.GetTransactions(parent, next.Header.GasLimit), warmedPerSender);
        return delta.Length == 0 ? null : new Block(next.Header, new BlockBody(delta, uncles: [], withdrawals: null));
    }

    /// <summary>
    /// Picks the transactions to warm on this delta pass from the producer's already gas-price-ordered, gas-limited,
    /// filtered selection, deduping per sender: a sender is skipped when all of its selected transactions are already
    /// warmed; otherwise its full selected set is replayed so later-nonce transactions get their predecessors' state.
    /// <paramref name="warmedPerSender"/> is updated in place with how far each sender has now been warmed.
    /// </summary>
    internal static Transaction[] SelectDelta(IEnumerable<Transaction> orderedTxs, Dictionary<AddressAsKey, int> warmedPerSender)
    {
        Dictionary<AddressAsKey, List<Transaction>> bySender = [];
        foreach (Transaction tx in orderedTxs)
        {
            if (tx.SenderAddress is not Address sender) continue;
            if (!bySender.TryGetValue(sender, out List<Transaction>? group))
            {
                group = new(4);
                bySender[sender] = group;
            }
            group.Add(tx);
        }

        List<Transaction> delta = [];
        foreach (KeyValuePair<AddressAsKey, List<Transaction>> senderGroup in bySender)
        {
            if (senderGroup.Value.Count <= warmedPerSender.GetValueOrDefault(senderGroup.Key)) continue;
            delta.AddRange(senderGroup.Value);
            warmedPerSender[senderGroup.Key] = senderGroup.Value.Count;
        }

        return delta.ToArray();
    }

    public void Dispose()
    {
        if (_enabled)
        {
            _blockTree.NewHeadBlock -= OnNewHeadBlock;
        }
        _cts.Cancel();
        _cts.Dispose();
    }

    private readonly record struct NextBlockContext(BlockHeader Header, IReleaseSpec Spec);
}
