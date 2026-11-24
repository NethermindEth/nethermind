// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;

namespace Nethermind.BeaconChain.Legacy;

/// <summary>A decoded signed block of a fork before Fulu.</summary>
/// <param name="BodyRoot">Computed when the block is decoded, because only the decoder knows the body's fork.</param>
internal sealed record LegacyBlock(SignedBeaconBlock Signed, Hash256 BodyRoot)
{
    public BeaconBlock Message => Signed.Message!;

    /// <summary>The block's header, which has the same root as the block.</summary>
    public SignedBeaconBlockHeader ToHeader() => new()
    {
        Message = new BeaconBlockHeader
        {
            Slot = Message.Slot,
            ProposerIndex = Message.ProposerIndex,
            ParentRoot = Message.ParentRoot,
            StateRoot = Message.StateRoot,
            BodyRoot = BodyRoot,
        },
        Signature = Signed.Signature,
    };
}
