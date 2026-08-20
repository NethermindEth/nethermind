// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Evm.State;

namespace Nethermind.Evm;

/// <summary>Pre-state validity of a frame transaction's EIP-8272 recent-root references.</summary>
public static class RecentRootReferences
{
    /// <summary>Checks every reference against the commitment in <c>RECENT_ROOT_ADDRESS</c>, warming the
    /// predeploy and the keys read (already paid for by intrinsic gas).</summary>
    /// <returns><see langword="true"/> when every reference is committed and inside the usable window.</returns>
    public static bool Validate(IWorldState state, RecentRootReference[]? references, ulong? currentSlot, in StackAccessTracker accessTracker)
    {
        if (references is null || references.Length == 0)
        {
            return true;
        }

        // References are anchored to slots, so a header without a slot number can place none of them.
        if (currentSlot is not { } slot || references.Length > Eip8272Constants.MaxRecentRootReferences)
        {
            return false;
        }

        accessTracker.WarmUp(Eip8272Constants.RecentRootAddress);
        foreach (RecentRootReference reference in references)
        {
            StorageCell cell = RecentRootStore.ReferenceCell(reference.SourceId, reference.Slot);
            accessTracker.WarmUp(cell);
            if (!RecentRootStore.IsReferenceValid(state, in cell, reference.SourceId, reference.Slot, reference.Root, slot))
            {
                return false;
            }
        }

        return true;
    }
}
