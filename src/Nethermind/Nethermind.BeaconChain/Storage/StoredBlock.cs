// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;

namespace Nethermind.BeaconChain.Storage;

/// <summary>The SSZ of a stored signed beacon block, whose fields are read without decoding the block.</summary>
/// <remarks>
/// Only the layout of the containers that are read is checked, so a successful read does not prove that the block decodes.
/// A malformed layout throws <see cref="System.IO.InvalidDataException"/>.
/// </remarks>
internal readonly struct StoredBlock(byte[] ssz, ulong slot, bool isGloas)
{
    public byte[] Ssz => ssz;
    public ulong Slot => slot;

    /// <summary>Whether the block has the Gloas shape, so that <see cref="Bid"/> can be read.</summary>
    public bool IsGloas => isGloas;

    public Hash256 ParentRoot => (isGloas ? Gloas.Message.ParentRoot : Fulu.Message.ParentRoot).ToHash256();
    public BlsSignature Signature => isGloas ? Gloas.Signature : Fulu.Signature;
    public ExecutionPayloadBid.RefReader Bid => Gloas.Message.Body.SignedExecutionPayloadBid.Message;
    public int BlobCommitmentCount => isGloas ? Bid.BlobKzgCommitments.Length : Fulu.Message.Body.BlobKzgCommitments.Length;

    private SignedBeaconBlock.RefReader Fulu => new(ssz);
    private SignedBeaconBlockGloas.RefReader Gloas => new(ssz);
}
