// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Evm;

/// <summary>
/// Gas and pre-state validity of the <see href="https://eips.ethereum.org/EIPS/eip-8272">EIP-8272</see>
/// recent-root references declared by a frame transaction.
/// </summary>
public static class RecentRootReferences
{
    /// <summary>
    /// The <c>recent_root_calldata</c> the references add to the payload, priced as frame data is.
    /// </summary>
    /// <remarks>
    /// Only an absent list is free: a present-but-empty list still encodes as <c>0xc0</c>, and EIP-8272
    /// short-circuits the per-reference intrinsic term at zero references but not the calldata term.
    /// </remarks>
    public static byte[] Calldata(RecentRootReference[]? references)
    {
        if (references is null)
        {
            return [];
        }

        byte[] bytes = new byte[RecentRootReferenceDecoder.Instance.GetArrayLength(references)];
        RlpWriter writer = new(bytes);
        RecentRootReferenceDecoder.Instance.EncodeArray(ref writer, references);
        return bytes;
    }

    /// <summary>
    /// The <c>recent_root_reference_intrinsic_gas</c> that prepays warming the predeploy and each derived
    /// storage key, plus the two Keccak computations deriving the key and the committed entry hash.
    /// </summary>
    /// <remarks>
    /// A mandatory cost charged outside the EIP-7623 floored term. The access-list rates are resolved from
    /// the spec, so a reference cannot be priced against a fork the transaction does not execute under.
    /// </remarks>
    public static ulong IntrinsicGas(RecentRootReference[]? references, IReleaseSpec spec)
    {
        if (references is null || references.Length == 0)
        {
            return 0;
        }

        ulong addressCost = spec.IsEip8038Enabled ? Eip8038Constants.AccessListAddressCost : GasCostOf.AccessAccountListEntry;
        ulong storageKeyCost = spec.IsEip8038Enabled ? Eip8038Constants.AccessListStorageKeyCost : GasCostOf.AccessStorageListEntry;
        ulong perReference = storageKeyCost + 2 * GasCostOf.Sha3 + 7 * GasCostOf.Sha3Word;
        return addressCost + (ulong)references.Length * perReference;
    }

    /// <summary>
    /// Checks every declared reference against the pre-state commitment in <c>RECENT_ROOT_ADDRESS</c> and
    /// warms the predeploy and the keys read, which the intrinsic gas has already paid for.
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
