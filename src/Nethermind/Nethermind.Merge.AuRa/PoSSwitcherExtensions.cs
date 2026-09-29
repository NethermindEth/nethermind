// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Core;

namespace Nethermind.Merge.AuRa;

internal static class PoSSwitcherExtensions
{
    // Check the current head rather than HasEverReachedTerminalBlock(): this disposer depends on
    // whether the head is post-merge, not only on durable TTD evidence from the chain's history.
    public static bool IsHeadPostMerge(this IPoSSwitcher poSSwitcher, IBlockTree blockTree)
    {
        BlockHeader? head = blockTree.Head?.Header;
        return head is not null && poSSwitcher.IsPostMerge(head);
    }
}
