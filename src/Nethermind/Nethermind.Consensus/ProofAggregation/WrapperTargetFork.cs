// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Consensus.ProofAggregation;

/// <summary>
/// Local wrapper target fork, which EIP-8288 does not specify: the fork active at <c>head.timestamp + L</c>, the timestamp of
/// the slot after the local head, even if that slot is missed.
/// </summary>
/// <remarks>
/// <c>L = (head.timestamp - parent.timestamp) // (head.slot_number - parent.slot_number)</c>, integer division over the
/// EIP-7843 slot numbers, is a local estimate for mempool validation only. Slot length is variable, so no configured slot length
/// is used: a head and parent straddling a slot-length change can give a stale or averaged estimate. Proof failures from peers
/// never incur penalties under EIP-8437, so such a misestimate only discards wrappers. Without a parent, or when slot numbers
/// do not increase, the target fork is the head's fork; so too, defensively, without slot numbers, which EIP-8288 never meets
/// since it activates after EIP-7843.
/// </remarks>
public static class WrapperTargetFork
{
    /// <summary>The current slot length <c>L</c> in seconds, or <c>null</c> without a parent, when the head or its parent has
    /// no slot number, or when <c>head.slot_number &lt;= parent.slot_number</c>.</summary>
    public static ulong? SlotLength(BlockHeader head, BlockHeader? parent)
        => head.SlotNumber is { } slot && parent?.SlotNumber is { } parentSlot && slot > parentSlot && head.Timestamp >= parent.Timestamp
            ? (head.Timestamp - parent.Timestamp) / (slot - parentSlot)
            : null;

    /// <summary>The fork active <paramref name="slots"/> slots after <paramref name="head"/> at the estimated slot length, or the
    /// head's fork without an estimate.</summary>
    /// <param name="slots">1 for the target fork; 2 for the fork after it.</param>
    public static IReleaseSpec Predict(ISpecProvider specProvider, BlockHeader head, BlockHeader? parent, ulong slots = 1)
        => SlotLength(head, parent) is { } length
            ? specProvider.GetSpec(head.Number + slots, head.Timestamp + slots * length)
            : specProvider.GetSpec(head);
}
