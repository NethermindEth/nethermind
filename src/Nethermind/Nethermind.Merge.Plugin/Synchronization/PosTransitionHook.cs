// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Synchronization.Blocks;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Merge.Plugin.Synchronization;

public class PosTransitionHook(IBlockTree blockTree, IPoSSwitcher poSSwitcher, ILogManager logManager) : IPosTransitionHook
{
    private readonly ILogger _logger = logManager.GetClassLogger<PosTransitionHook>();

    public void TryUpdateTerminalBlock(BlockHeader currentHeader)
    {
        poSSwitcher.TryUpdateTerminalBlock(currentHeader);
    }

    // Used only in get block header in pre merge block downloader, this hook stops pre merge block downloader
    // from downloader header once poSSwitcher.HasEverReachedTerminalBlock().
    public bool ImprovementRequirementSatisfied(PeerInfo? bestPeer)
    {
        return bestPeer!.TotalDifficulty > (blockTree.BestSuggestedHeader?.TotalDifficulty ?? 0) &&
               poSSwitcher.HasEverReachedTerminalBlock() == false;
    }

    public IOwnedReadOnlyList<BlockHeader> FilterPosHeader(IOwnedReadOnlyList<BlockHeader> response)
    {
        // Override PoW's RequestHeaders so that it won't request beyond PoW.
        // This fixes `Incremental Sync` hive test.
        if (response.Count > 0)
        {
            BlockHeader lastBlockHeader = response[^1];
            bool lastBlockIsPostMerge = poSSwitcher.GetBlockConsensusInfo(response[^1]).IsPostMerge;
            if (lastBlockIsPostMerge) // Initial check to prevent creating new array every time
            {
                IOwnedReadOnlyList<BlockHeader> oldResponse = response;
                response = response
                    .TakeWhile(header => !poSSwitcher.GetBlockConsensusInfo(header).IsPostMerge)
                    .ToPooledList(response.Count);
                oldResponse.Dispose();
                if (_logger.IsInfo) _logger.Info($"Last block is post merge. {lastBlockHeader.Hash}. Trimming to {response.Count} sized batch.");
            }
        }
        return response;
    }
}
