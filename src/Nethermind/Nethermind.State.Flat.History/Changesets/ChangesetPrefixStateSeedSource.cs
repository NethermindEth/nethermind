// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;

namespace Nethermind.State.Flat.History.Changesets;

/// <summary>Seeds a trace from the changeset index: the state before transaction T of a covered block is the state at
/// the parent read through the writes of transactions 0..T-1. The overlay is armed on the trace's own read path, so a
/// key the target never reads is never touched, and the parent state is never changed.</summary>
public sealed class ChangesetPrefixStateSeedSource(TransactionChangesetIndex index) : IPrefixStateSeedSource
{
    public bool TrySeed(Block block, int transactionIndex, StateReadOverlaySlot slot)
    {
        if (transactionIndex <= 0 || transactionIndex > ChangesetKeyLayout.MaxTransactionIndex || block.Hash is null) return false;
        if (!index.TryRentOverlay((ulong)block.Number, block.Hash, (ushort)transactionIndex, out MidBlockOverlayCache.Lease lease)) return false;

        slot.Arm(new MidBlockReadOverlay(lease.Overlay), lease);
        return true;
    }
}
