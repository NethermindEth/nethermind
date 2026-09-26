// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Eez.Execution.Settlement;

/// <summary>
/// The batch's claimed L2 block hashes must chain from the window's first parent to its last block: every entry
/// updates the one rollup once, starting where the previous entry ended.
/// </summary>
public static class StateUpdateChain
{
    /// <returns>The sole state update of each entry, in entry order.</returns>
    /// <exception cref="EezSettlementException">The updates do not form that chain.</exception>
    public static StateUpdate[] Verify(PostBatch batch, ulong rollupId, in ValueHash256 windowPreBlockHash, in ValueHash256 windowPostBlockHash)
    {
        if (batch.Entries.Length == 0)
        {
            throw new EezSettlementException("The batch has no execution entries.");
        }

        StateUpdate[] updates = new StateUpdate[batch.Entries.Length];
        ValueHash256 previous = windowPreBlockHash;
        for (int i = 0; i < updates.Length; i++)
        {
            if (batch.Entries[i].StateUpdates is not [{ } update])
            {
                throw new EezSettlementException($"Entry {i} must carry exactly one state update.");
            }

            if (update.RollupId != rollupId)
            {
                throw new EezSettlementException($"Entry {i} updates rollup {update.RollupId}, not rollup {rollupId}.");
            }

            if (update.CurrentState != previous)
            {
                throw new EezSettlementException($"Entry {i} starts from {update.CurrentState}, not from {previous}.");
            }

            updates[i] = update;
            previous = update.NewState;
        }

        if (previous != windowPostBlockHash)
        {
            throw new EezSettlementException($"The batch ends at {previous}, not at the window's last block {windowPostBlockHash}.");
        }

        return updates;
    }
}
