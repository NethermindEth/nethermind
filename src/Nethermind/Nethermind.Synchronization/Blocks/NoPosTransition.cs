// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Synchronization.Blocks;

public class NoPosTransition(IBlockTree blockTree) : IPosTransitionHook
{
    public void TryUpdateTerminalBlock(BlockHeader currentHeader, bool shouldProcess)
    {
    }

    public bool ImprovementRequirementSatisfied(PeerInfo? bestPeer)
    {
        return bestPeer!.TotalDifficulty > (blockTree.BestSuggestedHeader?.TotalDifficulty ?? 0);
    }

    public IOwnedReadOnlyList<BlockHeader> FilterPosHeader(IOwnedReadOnlyList<BlockHeader> headers)
    {
        return headers;
    }
}
