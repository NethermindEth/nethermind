// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Synchronization.Blocks;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Reporting;

namespace Nethermind.Merge.Plugin.Synchronization;

public class PosForwardHeaderProvider(
    IChainLevelHelper chainLevelHelper,
    IPoSSwitcher poSSwitcher,
    IBeaconPivot beaconPivot,
    ISealValidator sealValidator,
    IBlockTree blockTree,
    ISyncPeerPool syncPeerPool,
    ISyncReport syncReport,
    ILogManager logManager
) : PowForwardHeaderProvider(sealValidator, blockTree, syncPeerPool, syncReport, logManager)
{
    private const int CacheBatchMultiplier = 4;
    private const int MinCachedHeaderBatchSize = 32;

    private readonly ILogger _logger = logManager.GetClassLogger<PosForwardHeaderProvider>();
    private readonly IBlockTree _blockTree = blockTree;
    private readonly ISyncReport _syncReport = syncReport;

    private readonly Lock _cacheLock = new();
    private BlockHeader[]? _cachedHeaders;
    private Hash256? _cachedProcessDestinationHash;
    private long _cachedProcessDestinationNumber;

    private bool ShouldUsePreMerge() => !beaconPivot.BeaconPivotExists() && !poSSwitcher.HasEverReachedTerminalBlock();

    public override Task<IOwnedReadOnlyList<BlockHeader?>?> GetBlockHeaders(int skipLastN, int maxHeader, CancellationToken cancellation)
    {
        if (ShouldUsePreMerge())
        {
            return base.GetBlockHeaders(skipLastN, maxHeader, cancellation);
        }

        _syncReport.FullSyncBlocksDownloaded.TargetValue = Math.Max(beaconPivot.PivotNumber, beaconPivot.PivotDestinationNumber);

        BlockHeader?[]? slice = TryServeFromCache(maxHeader, skipLastN);
        if (slice is not null)
        {
            if (_logger.IsTrace) _logger.Trace($"Served {slice.Length} headers from forward-header cache");
            return Task.FromResult<IOwnedReadOnlyList<BlockHeader?>?>(slice.ToPooledList(slice.Length));
        }

        // Fetch a larger batch than asked so subsequent peer allocations can be served from the cache.
        int fetchSize = Math.Max(maxHeader * CacheBatchMultiplier, MinCachedHeaderBatchSize);
        BlockHeader?[]? fresh = chainLevelHelper.GetNextHeaders(fetchSize, long.MaxValue, skipLastBlockCount: 0);
        if (fresh is null || fresh.Length <= 1)
        {
            if (_logger.IsTrace) _logger.Trace("Chain level helper got no headers suggestion");
            return Task.FromResult<IOwnedReadOnlyList<BlockHeader?>?>(null);
        }

        // Alternatively we can do this in BeaconHeadersSyncFeed, but this seems easier.
        ValidateSeals(fresh!, cancellation);

        UpdateCache(fresh!);

        return Task.FromResult<IOwnedReadOnlyList<BlockHeader?>?>(BuildSlice(fresh!, maxHeader, skipLastN));
    }

    private BlockHeader?[]? TryServeFromCache(int maxHeader, int skipLastN)
    {
        BlockHeader[]? cached;
        Hash256? cachedHash;
        long cachedNumber;
        lock (_cacheLock)
        {
            cached = _cachedHeaders;
            cachedHash = _cachedProcessDestinationHash;
            cachedNumber = _cachedProcessDestinationNumber;
        }

        if (cached is null || cached.Length == 0) return null;

        BlockHeader? processDestination = beaconPivot.ProcessDestination;
        Hash256? currentHash = processDestination?.Hash;
        long currentNumber = processDestination?.Number ?? long.MaxValue;
        if (cachedHash != currentHash || cachedNumber != currentNumber) return null;

        long desiredStart = Math.Min(_blockTree.BestKnownNumber + 1, currentNumber);
        long cacheStart = cached[0].Number;
        long cacheEnd = cached[^1].Number;
        if (desiredStart < cacheStart || desiredStart > cacheEnd) return null;

        int offset = (int)(desiredStart - cacheStart);
        int available = cached.Length - offset - skipLastN;
        if (available <= 1) return null;

        int take = Math.Min(available, maxHeader);
        BlockHeader?[] slice = new BlockHeader[take];
        Array.Copy(cached, offset, slice!, 0, take);
        return slice;
    }

    private void UpdateCache(BlockHeader[] headers)
    {
        if (headers.Length < MinCachedHeaderBatchSize) return;

        BlockHeader? destination = beaconPivot.ProcessDestination;
        lock (_cacheLock)
        {
            _cachedHeaders = headers;
            _cachedProcessDestinationHash = destination?.Hash;
            _cachedProcessDestinationNumber = destination?.Number ?? long.MaxValue;
        }
    }

    private static IOwnedReadOnlyList<BlockHeader?> BuildSlice(BlockHeader?[] fresh, int maxHeader, int skipLastN)
    {
        int take = Math.Max(0, Math.Min(fresh.Length - skipLastN, maxHeader));
        if (take == fresh.Length)
        {
            return fresh.ToPooledList(fresh.Length);
        }

        ArrayPoolList<BlockHeader?> result = new(take);
        for (int i = 0; i < take; i++) result.Add(fresh[i]);
        return result;
    }

    private void TryUpdateTerminalBlock(BlockHeader currentHeader) =>
        // Needed to know what is the terminal block so in fast sync, for each
        // header, it calls this.
        poSSwitcher.TryUpdateTerminalBlock(currentHeader);

    // Used only in get block header in pre merge forward header provider, this hook stops pre merge forward header provider.
    protected override bool ImprovementRequirementSatisfied(PeerInfo? bestPeer) => (bestPeer!.TotalDifficulty is null || bestPeer.TotalDifficulty > (_blockTree.BestSuggestedHeader?.TotalDifficulty ?? UInt256.Zero)) &&
            !poSSwitcher.HasEverReachedTerminalBlock();

    protected override IOwnedReadOnlyList<BlockHeader> FilterPosHeader(IOwnedReadOnlyList<BlockHeader> response)
    {
        // Override PoW's RequestHeaders so that it won't request beyond PoW.
        // This fixes `Incremental Sync` hive test.
        ReadOnlySpan<BlockHeader> responseSpan = response.AsSpan();
        if (responseSpan.Length > 0)
        {
            BlockHeader lastBlockHeader = responseSpan[^1];
            bool lastBlockIsPostMerge = poSSwitcher.GetBlockConsensusInfo(responseSpan[^1]).IsPostMerge;
            if (lastBlockIsPostMerge) // Initial check to prevent creating new array every time
            {
                int preMergeHeadersCount = 0;
                while (preMergeHeadersCount < responseSpan.Length && !poSSwitcher.GetBlockConsensusInfo(responseSpan[preMergeHeadersCount]).IsPostMerge)
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
            if ((beaconPivot.ProcessDestination?.Number ?? long.MaxValue) < currentBlock.Number)
            {
                // Move the process destination in front, otherwise `ChainLevelHelper` would continue returning
                // already processed header starting from `ProcessDestination`.
                beaconPivot.ProcessDestination = currentBlock.Header;
            }
        }
    }
}
