// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Logging;

namespace Nethermind.Consensus.Processing;

/// <summary>
/// During no-CL / catch-up sync, speculatively warms the state caches for the next already-downloaded block(s)
/// against the current head, using idle cores while the head block commits. The warmed caches are handed off to the
/// reactive prewarmer via the same <see cref="IBlockCachePreWarmer"/> marker used by the mempool speculative path.
/// </summary>
/// <remarks>
/// Mutually exclusive with <see cref="MempoolStatePrewarmer"/>: this runs only while the head is stale (catching up),
/// the mempool warmer only while the head is fresh (at tip). Depth 1 is coherence-safe because the warmed base (the
/// current head) is exactly the next block's parent, so the handoff marker matches on entry to processing. Depth &gt; 1
/// only usefully warms content-addressed trie/node caches; value-cache entries for grandchildren are discarded when
/// their marker parent mismatches (safe, just wasted).
/// </remarks>
public sealed class SyncBlockAheadPrewarmer : IDisposable
{
    private const int IdlePassDelayMs = 50;

    private readonly IBlockCachePreWarmer _preWarmer;
    private readonly IBlockTree _blockTree;
    private readonly ISpecProvider _specProvider;
    private readonly ITimestamper _timestamper;
    private readonly ILogger _logger;
    private readonly ulong _maxHeadAgeSeconds;
    private readonly int _depth;
    private readonly bool _enabled;
    private readonly CancellationTokenSource _cts = new();
    private int _disposed;

    // Monotonic: a queued pass runs only while it still reflects the latest head.
    private long _generation;

    public SyncBlockAheadPrewarmer(
        IBlockCachePreWarmer preWarmer,
        IBlockTree blockTree,
        ISpecProvider specProvider,
        ITimestamper timestamper,
        IBlocksConfig blocksConfig,
        ILogManager logManager)
    {
        _preWarmer = preWarmer;
        _blockTree = blockTree;
        _specProvider = specProvider;
        _timestamper = timestamper;
        _logger = logManager.GetClassLogger<SyncBlockAheadPrewarmer>();
        _maxHeadAgeSeconds = Math.Max(1UL, blocksConfig.SecondsPerSlot) * 4;
        _depth = Math.Max(1, blocksConfig.SyncBlockAheadDepth);
        _enabled = blocksConfig.SyncBlockAheadPrewarming && blocksConfig.PreWarming != PreWarmMode.None;

        if (_enabled)
        {
            _blockTree.NewHeadBlock += OnNewHeadBlock;
            if (_logger.IsDebug) _logger.Debug("Sync block-ahead pre-warming enabled.");
        }
    }

    private void OnNewHeadBlock(object? sender, BlockEventArgs e)
    {
        Block head = e.Block;

        // Run only while catching up: a fresh head means we are at tip and MempoolStatePrewarmer owns the idle gap.
        if (head.Header.Timestamp + _maxHeadAgeSeconds >= _timestamper.UnixTime.Seconds) return;

        long generation = Interlocked.Increment(ref _generation);
        // Queue off the notification thread so head updates are never delayed.
        ThreadPool.UnsafeQueueUserWorkItem(
            static state => state.self.PreWarmAhead(state.head, state.generation),
            (self: this, head, generation),
            preferLocal: false);
    }

    private void PreWarmAhead(Block head, long generation)
    {
        try
        {
            if (IsStale(generation)) return;

            BlockHeader headHeader = head.Header;
            ulong number = headHeader.Number + 1;
            ulong timestamp = Math.Max(headHeader.Timestamp + 1, _timestamper.UnixTime.Seconds);
            IReleaseSpec spec = _specProvider.GetSpec(new ForkActivation(number, timestamp));

            int warmed = 0;
            _preWarmer.StartSpeculativePreWarm(
                headHeader,
                spec,
                generation,
                token =>
                {
                    if (token.IsCancellationRequested || IsStale(generation) || warmed >= _depth) return null;
                    ulong target = number + (ulong)warmed;
                    warmed++;
                    Block? best = _blockTree.BestSuggestedBody;
                    if (best is not null && target > (ulong)best.Number) return null;
                    return _blockTree.FindBlock(target, BlockTreeLookupOptions.None);
                },
                IdlePassDelayMs,
                _cts.Token);
        }
        catch (Exception ex)
        {
            if (_logger.IsDebug) _logger.Debug($"Error starting sync block-ahead pre-warm for head {head.Number}: {ex}");
        }
    }

    private bool IsStale(long generation) => Volatile.Read(ref _generation) != generation;

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
}
