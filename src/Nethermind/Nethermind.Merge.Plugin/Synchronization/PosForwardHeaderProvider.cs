// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Collections;
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
    private readonly ILogger _logger = logManager.GetClassLogger<PosForwardHeaderProvider>();
    private readonly IBlockTree _blockTree = blockTree;
    private readonly ISyncReport _syncReport = syncReport;

    private bool ShouldUsePreMerge() => !beaconPivot.BeaconPivotExists() && !poSSwitcher.HasEverReachedTerminalBlock();

    public override Task<IOwnedReadOnlyList<BlockHeader?>?> GetBlockHeaders(int skipLastN, int maxHeader, CancellationToken cancellation)
    {
        if (ShouldUsePreMerge())
        {
            return base.GetBlockHeaders(skipLastN, maxHeader, cancellation);
        }

        _syncReport.FullSyncBlocksDownloaded.TargetValue = Math.Max(beaconPivot.PivotNumber, beaconPivot.PivotDestinationNumber);

        // TODO: Previously it does not get block more than best peer's head number. Why?
        BlockHeader?[]? headers = chainLevelHelper.GetNextHeaders(maxHeader, long.MaxValue, skipLastN);
        if (headers is null || headers.Length <= 1)
        {
            if (_logger.IsTrace) _logger.Trace("Chain level helper got no headers suggestion");
            return Task.FromResult<IOwnedReadOnlyList<BlockHeader?>?>(null);
        }

        // Alternatively we can do this in BeaconHeadersSyncFeed, but this seems easier.
        ValidateSeals(headers!, cancellation);

        return Task.FromResult<IOwnedReadOnlyList<BlockHeader?>?>(headers.ToPooledList(headers.Length));
    }

    private void TryUpdateTerminalBlock(BlockHeader currentHeader)
    {
        // Needed to know what is the terminal block so in fast sync, for each
        // header, it calls this.
        poSSwitcher.TryUpdateTerminalBlock(currentHeader);
    }

    // Used only in get block header in pre merge forward header provider, this hook stops pre merge forward header provider.
    protected override bool ImprovementRequirementSatisfied(PeerInfo? bestPeer)
    {
        return
            (bestPeer!.TotalDifficulty is null || bestPeer.TotalDifficulty > (_blockTree.BestSuggestedHeader?.TotalDifficulty ?? UInt256.Zero)) &&
            !poSSwitcher.HasEverReachedTerminalBlock();
    }

    protected override IOwnedReadOnlyList<BlockHeader> FilterPosHeader(IOwnedReadOnlyList<BlockHeader> response)
    {
        // Override PoW's RequestHeaders so that it won't request beyond PoW.
        // This fixes `Incremental Sync` hive test.
        if (response.Count > 0)
        {
            BlockHeader lastBlockHeader = response[^1];
            bool lastBlockIsPostMerge = poSSwitcher.GetBlockConsensusInfo(response[^1]).IsPostMerge;
            if (lastBlockIsPostMerge) // Initial check to prevent creating new array every time
            {
                using IOwnedReadOnlyList<BlockHeader> oldResponse = response;
                response = response
                    .TakeWhile(header => !poSSwitcher.GetBlockConsensusInfo(header).IsPostMerge)
                    .ToPooledList(response.Count);
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
