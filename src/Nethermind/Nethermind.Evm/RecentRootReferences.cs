// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Evm.State;

namespace Nethermind.Evm;

/// <summary>
/// Pre-state validity of the <see href="https://eips.ethereum.org/EIPS/eip-8272">EIP-8272</see>
/// recent-root references declared by a frame transaction.
/// </summary>
public static class RecentRootReferences
{
    /// <summary>
    /// Checks every declared reference against the pre-state commitment in <c>RECENT_ROOT_ADDRESS</c> and
    /// warms the predeploy and the keys read, for which the intrinsic gas has paid the access-list pre-warm
    /// rate; the reads themselves are uncharged.
    /// </summary>
    /// <remarks>
    /// A reference at or ahead of <paramref name="currentSlot"/> is not yet referenceable and one older than
    /// the usable window has been overwritten by ring-buffer aliasing; both fail, as does a commitment that
    /// does not match. The reads are real predeploy reads rather than declared access-list entries, so they
    /// belong in the block access list once the whole pass succeeds — a failed reference invalidates the
    /// transaction and the block carrying it.
    /// </remarks>
    /// <returns><see langword="true"/> when every reference is committed and in range.</returns>
    public static bool Validate(IWorldState state, RecentRootReference[]? references, ulong? currentSlot, in StackAccessTracker accessTracker)
    {
        if (references is null || references.Length == 0)
        {
            return true;
        }

        // References are anchored to slots, so a header that carries no slot number cannot place them
        // in the window at all.
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
