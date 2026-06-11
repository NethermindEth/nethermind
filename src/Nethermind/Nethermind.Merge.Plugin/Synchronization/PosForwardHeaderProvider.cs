// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Synchronization.Blocks;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Reporting;

namespace Nethermind.Merge.Plugin.Synchronization;

public class PosForwardHeaderProvider : PowForwardHeaderProvider
{
    private const int CacheBatchMultiplier = 4;

    private readonly IChainLevelHelper _chainLevelHelper;
    private readonly IPoSSwitcher _poSSwitcher;
    private readonly IBeaconPivot _beaconPivot;
    private readonly ILogger _logger;
    private readonly IBlockTree _blockTree;
    private readonly ISyncReport _syncReport;

    private readonly Lock _cacheLock = new();
    private BlockHeader[]? _cachedHeaders;
    private Hash256? _cachedProcessDestinationHash;
    private ulong _cachedProcessDestinationNumber;
    private int _cachedSkipLastN;

    public PosForwardHeaderProvider(
        IChainLevelHelper chainLevelHelper,
        IPoSSwitcher poSSwitcher,
        IBeaconPivot beaconPivot,
        ISealValidator sealValidator,
        IBlockTree blockTree,
        ISyncPeerPool syncPeerPool,
        ISyncReport syncReport,
        ILogManager logManager)
        : base(sealValidator, blockTree, syncPeerPool, syncReport, logManager)
    {
        _chainLevelHelper = chainLevelHelper;
        _poSSwitcher = poSSwitcher;
        _beaconPivot = beaconPivot;
        _blockTree = blockTree;
        _syncReport = syncReport;
        _logger = logManager.GetClassLogger<PosForwardHeaderProvider>();

        // Invalidate the cache on reorgs that don't change `BeaconPivot.ProcessDestination`.
        _blockTree.OnUpdateMainChain += BlockTreeOnUpdateMainChain;
    }

    private bool ShouldUsePreMerge() => !_beaconPivot.BeaconPivotExists() && !_poSSwitcher.HasEverReachedTerminalBlock();

    public override Task<IOwnedReadOnlyList<BlockHeader>?> GetBlockHeaders(ulong skipLastNUlong, ulong maxHeaderUlong, CancellationToken cancellation)
    {
        if (ShouldUsePreMerge())
        {
            return base.GetBlockHeaders(skipLastNUlong, maxHeaderUlong, cancellation);
        }

        int skipLastN = (int)skipLastNUlong;
        int maxHeader = (int)maxHeaderUlong;

        _syncReport.FullSyncBlocksDownloaded.TargetValue = Math.Max(_beaconPivot.PivotNumber, _beaconPivot.PivotDestinationNumber);

        ArrayPoolList<BlockHeader>? slice = TryServeFromCache(maxHeader, skipLastN);
        if (slice is not null)
        {
            try
            {
                // Re-validate per slice so terminal-block / random-index checks run on the served window
                // rather than only at fill time.
                ValidateSeals(slice, cancellation);
            }
            catch
            {
                slice.Dispose();
                throw;
            }
            if (_logger.IsTrace) _logger.Trace($"Served {slice.Count} headers from forward-header cache");
            return Task.FromResult<IOwnedReadOnlyList<BlockHeader>?>(slice);
        }

        // Fetch a larger batch than asked so subsequent peer allocations can be served from the cache.
        int fetchSize = Math.Max(maxHeader * CacheBatchMultiplier, MinCachedHeaderBatchSize);
        // Forward `skipLastN` so `ChainLevelHelper` enforces the same chain-tip exclusion as the
        // pre-cache implementation; trim the slice tail again at serve time to honour per-call values.
        BlockHeader[]? fresh = _chainLevelHelper.GetNextHeaders(fetchSize, long.MaxValue, skipLastBlockCount: skipLastN);
        if (fresh is null || fresh.Length <= 1)
        {
            if (_logger.IsTrace) _logger.Trace("Chain level helper got no headers suggestion");
            return Task.FromResult<IOwnedReadOnlyList<BlockHeader>?>(null);
        }

        // Alternatively we can do this in BeaconHeadersSyncFeed, but this seems easier.
        ValidateSeals(fresh, cancellation);

        // Only cache a full-sized batch; a truncated fetch implies we reached the chain tip and the
        // cached tail would diverge from the original `skipLastBlockCount` semantics on later calls.
        if (fresh.Length >= fetchSize) UpdateCache(fresh, skipLastN);

        int take = Math.Min(fresh.Length, maxHeader);
        ArrayPoolList<BlockHeader> result = new(fresh.AsSpan(0, take));
        return Task.FromResult<IOwnedReadOnlyList<BlockHeader>?>(result);
    }

    private ArrayPoolList<BlockHeader>? TryServeFromCache(int maxHeader, int skipLastN)
    {
        BlockHeader[] cached;
        int offset;
        int take;
        lock (_cacheLock)
        {
            if (_cachedHeaders is null || _cachedSkipLastN != skipLastN) return null;

            BlockHeader? processDestination = _beaconPivot.ProcessDestination;
            Hash256? currentHash = processDestination?.Hash;
            ulong currentNumber = processDestination?.Number ?? ulong.MaxValue;
            if (_cachedProcessDestinationHash != currentHash || _cachedProcessDestinationNumber != currentNumber) return null;

            cached = _cachedHeaders;
            // `cached[0]` is the anchor that `BlockDownloader.AssembleRequest` consumes as `parentHeader`;
            // start at `BestKnownNumber` (not `+1`) so the anchor stays at slice index 0.
            ulong desiredStart = Math.Min(_blockTree.BestKnownNumber, currentNumber);
            ulong cacheStart = cached[0]!.Number;
            ulong cacheEnd = cached[^1]!.Number;
            if (desiredStart < cacheStart || desiredStart > cacheEnd) return null;

            offset = (int)(desiredStart - cacheStart);
            int available = cached.Length - offset;
            if (available <= 1) return null;

            take = Math.Min(available, maxHeader);
        }

        return new ArrayPoolList<BlockHeader>(cached.AsSpan(offset, take)!);
    }

    private void UpdateCache(BlockHeader[] headers, int skipLastN)
    {
        BlockHeader? destination = _beaconPivot.ProcessDestination;
        lock (_cacheLock)
        {
            _cachedHeaders = headers;
            _cachedProcessDestinationHash = destination?.Hash;
            _cachedProcessDestinationNumber = destination?.Number ?? ulong.MaxValue;
            _cachedSkipLastN = skipLastN;
        }
    }

    private void BlockTreeOnUpdateMainChain(object? sender, OnUpdateMainChainArgs e)
    {
        IReadOnlyList<BlockHeader> headers = e.Headers;
        if (headers.Count == 0) return;

        lock (_cacheLock)
        {
            BlockHeader[]? cached = _cachedHeaders;
            if (cached is null) return;

            ulong cacheStart = cached[0]!.Number;
            ulong cacheEnd = cached[^1]!.Number;

            // Reorgs may start below `cacheStart`; scan until we hit a block inside the cached range.
            for (int i = 0; i < headers.Count; i++)
            {
                BlockHeader block = headers[i];
                if (block.Number < cacheStart || block.Number > cacheEnd) continue;

                int idx = (int)(block.Number - cacheStart);
                if (cached[idx]!.Hash != block.Hash)
                {
                    _cachedHeaders = null;
                    _cachedProcessDestinationHash = null;
                    _cachedProcessDestinationNumber = 0UL;
                    _cachedSkipLastN = 0;
                }
                // First in-range block's hash commits to all earlier blocks in this update.
                return;
            }
        }
    }

    private void TryUpdateTerminalBlock(BlockHeader currentHeader) =>
        // Needed to know what is the terminal block so in fast sync, for each
        // header, it calls this.
        _poSSwitcher.TryUpdateTerminalBlock(currentHeader);

    // Used only in get block header in pre merge forward header provider, this hook stops pre merge forward header provider.
    protected override bool ImprovementRequirementSatisfied(PeerInfo? bestPeer) => (bestPeer!.TotalDifficulty is null || bestPeer.TotalDifficulty > (_blockTree.BestSuggestedHeader?.TotalDifficulty ?? UInt256.Zero)) &&
            !_poSSwitcher.HasEverReachedTerminalBlock();

    protected override IOwnedReadOnlyList<BlockHeader> FilterPosHeader(IOwnedReadOnlyList<BlockHeader> response)
    {
        // Override PoW's RequestHeaders so that it won't request beyond PoW.
        // This fixes `Incremental Sync` hive test.
        ReadOnlySpan<BlockHeader> responseSpan = response.AsSpan();
        if (responseSpan.Length > 0)
        {
            BlockHeader lastBlockHeader = responseSpan[^1];
            bool lastBlockIsPostMerge = _poSSwitcher.GetBlockConsensusInfo(responseSpan[^1]).IsPostMerge;
            if (lastBlockIsPostMerge) // Initial check to prevent creating new array every time
            {
                int preMergeHeadersCount = 0;
                while (preMergeHeadersCount < responseSpan.Length && !_poSSwitcher.GetBlockConsensusInfo(responseSpan[preMergeHeadersCount]).IsPostMerge)
                {
                    preMergeHeadersCount++;
                }

                using IOwnedReadOnlyList<BlockHeader> oldResponse = response;
                ArrayPoolList<BlockHeader> trimmedResponse = new(preMergeHeadersCount);
                trimmedResponse.AddRange(responseSpan[..preMergeHeadersCount]);
                response = trimmedResponse;
                if (_logger.IsInfo) _logger.Info($"Last block is post merge. {lastBlockHeader.Hash}. Trimming to {response.Count} sized batch.");
            }
        }
        return response;
    }

    public override void OnSuggestBlock(BlockTreeSuggestOptions options, Block currentBlock, AddBlockResult addResult)
    {
        base.OnSuggestBlock(options, currentBlock, addResult);

        if ((options & BlockTreeSuggestOptions.ShouldProcess) == 0)
        {
            // Needed to know if a block is the terminal block.
            // Not needed if not processing for some reason.
            TryUpdateTerminalBlock(currentBlock.Header);
        }

        if (addResult == AddBlockResult.Added)
        {
            if ((_beaconPivot.ProcessDestination?.Number ?? ulong.MaxValue) < currentBlock.Number)
            {
                // Move the process destination in front, otherwise `ChainLevelHelper` would continue returning
                // already processed header starting from `ProcessDestination`.
                _beaconPivot.ProcessDestination = currentBlock.Header;
            }
        }
    }

    internal void UnsubscribeForTest() => _blockTree.OnUpdateMainChain -= BlockTreeOnUpdateMainChain;
}
