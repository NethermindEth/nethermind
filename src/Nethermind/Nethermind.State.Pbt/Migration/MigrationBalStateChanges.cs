// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Processing;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;

namespace Nethermind.State.Pbt.Migration;

/// <summary>Applies validated final BAL deltas to an already-open migration world state without executing transactions.</summary>
internal static class MigrationBalStateChanges
{
    /// <summary>Commits final account and storage changes; the caller owns root calculation and publication.</summary>
    internal static void Apply(ReadOnlyBlockAccessList blockAccessList, IWorldState worldState, IReleaseSpec spec) =>
        BlockAccessListManager.ApplyStateChanges(blockAccessList, worldState, spec, shouldComputeStateRoot: false);
}
