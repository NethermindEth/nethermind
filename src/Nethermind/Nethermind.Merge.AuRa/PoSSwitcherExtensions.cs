// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Core;

namespace Nethermind.Merge.AuRa;

internal static class PoSSwitcherExtensions
{
    // Not HasEverReachedTerminalBlock(): that flag is true on a fresh archive DB with
    // Merge.FinalTotalDifficulty in config, even at genesis.
    public static bool IsHeadPostMerge(this IPoSSwitcher poSSwitcher, IBlockTree blockTree)
    {
        BlockHeader? head = blockTree.Head?.Header;
        return head is not null && poSSwitcher.IsPostMerge(head);
    }
}
