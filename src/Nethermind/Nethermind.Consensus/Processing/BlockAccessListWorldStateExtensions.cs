// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;

namespace Nethermind.Consensus.Processing;

public static class BlockAccessListWorldStateExtensions
{
    /// <summary>Lays a block's access list post-state over the world state and recalculates the state root.</summary>
    /// <remarks>
    /// Pending changes are committed first, since <see cref="IWorldState.ApplyBal"/> drops anything uncommitted.
    /// The world state never sees the list's writes, so callers take changed accounts from the list itself.
    /// </remarks>
    public static void ApplyBlockAccessList(this IWorldState state, ReadOnlyBlockAccessList blockAccessList, IReleaseSpec spec)
    {
        state.Commit(spec);
        state.ApplyBal(blockAccessList);
        state.RecalculateStateRoot();
    }
}
