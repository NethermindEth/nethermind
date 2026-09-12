// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Specs;

namespace Nethermind.Evm.State;

/// <summary>
/// A world state that can bulk-apply a block's BAL post-values straight to the backend scope,
/// bypassing the per-operation journal.
/// </summary>
public interface IBalBulkWorldState
{
    /// <summary>
    /// Applies the BAL's final per-account/per-slot values (accounts via <see cref="BalPostState"/>,
    /// last value per changed slot, code inserts) directly through the backend scope's write batch.
    /// </summary>
    /// <remarks>
    /// Preconditions: an open scope, and no state mutations performed through this world state in
    /// the current block — the bulk write bypasses the journal, so it must be the block's only
    /// write to this instance up to this point. Storage roots are reconciled by the batch; the
    /// caller computes the state root afterwards (e.g. <see cref="IWorldState.RecalculateStateRoot"/>).
    /// Block-level change tracking (<see cref="IWorldState.GetAccountChanges"/>) is kept accurate.
    /// </remarks>
    void BulkApplyBal(ReadOnlyBlockAccessList bal, IReleaseSpec spec);
}
