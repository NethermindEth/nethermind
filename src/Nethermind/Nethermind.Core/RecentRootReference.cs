// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;

namespace Nethermind.Core;

/// <summary>
/// A recent-root reference declared by a frame transaction: <c>[source_id, slot, root]</c>.
/// https://eips.ethereum.org/EIPS/eip-8272
/// </summary>
/// <remarks>The root is opaque to consensus — applications bind its meaning. The slot is a beacon slot number.</remarks>
public class RecentRootReference(in ValueHash256 sourceId, ulong slot, in ValueHash256 root)
{
    public ValueHash256 SourceId { get; } = sourceId;
    public ulong Slot { get; } = slot;
    public ValueHash256 Root { get; } = root;

    /// <summary>
    /// The <c>recent_root_reference_intrinsic_gas</c> that pre-warms the predeploy and each derived storage
    /// key, plus the two Keccak computations deriving the key and the committed entry hash.
    /// </summary>
    /// <remarks>
    /// A mandatory cost charged outside the EIP-7623 floored term. EIP-8272 prices the pre-warm at the
    /// access-list entry rates — cold access less the warm charge — so the validation reads that follow are
    /// themselves uncharged. Before EIP-8272 a reference warms no predeploy and derives no key, so it costs
    /// nothing.
    /// </remarks>
    public static ulong IntrinsicGas(RecentRootReference[]? references, IReleaseSpec spec)
    {
        if (references is null || references.Length == 0 || !spec.IsEip8272Enabled)
        {
            return 0;
        }

        (ulong addressCost, ulong storageKeyCost) = Eip8038Constants.AccessListEntryCosts(spec);
        ulong perReference = storageKeyCost + 2 * GasCostOf.Sha3 + 7 * GasCostOf.Sha3Word;
        return addressCost + (ulong)references.Length * perReference;
    }
}
