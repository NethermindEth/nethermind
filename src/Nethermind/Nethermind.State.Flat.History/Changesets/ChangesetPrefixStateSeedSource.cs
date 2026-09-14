// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;

namespace Nethermind.State.Flat.History.Changesets;

/// <summary>Seeds a trace's world state from the changeset index: the state before transaction T of a covered block
/// is the state at the parent with the writes of transactions 0..T-1 laid over it.</summary>
public sealed class ChangesetPrefixStateSeedSource(TransactionChangesetIndex index) : IPrefixStateSeedSource
{
    public bool TrySeed(Block block, int transactionIndex, IWorldState state, IReleaseSpec spec)
    {
        if (transactionIndex <= 0 || transactionIndex > ChangesetKeyLayout.MaxTransactionIndex || block.Hash is null) return false;
        if (!index.TryRentOverlay((ulong)block.Number, block.Hash, (ushort)transactionIndex, out MidBlockOverlayCache.Lease lease)) return false;

        using (lease)
        {
            return PrefixStateSeeder.TryApply(lease.Overlay, state, spec);
        }
    }
}
