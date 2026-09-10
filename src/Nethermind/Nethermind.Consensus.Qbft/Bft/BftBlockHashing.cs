// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Consensus.Qbft.Bft;

/// <summary>
/// The two BFT header digests: the on-chain block hash (extra data with round zeroed and seals
/// removed) and the committed-seal digest (extra data with seals removed but the round kept).
/// </summary>
/// <remarks>
/// Both are the keccak of the header RLP with the <c>extraData</c> field re-encoded; genesis is the
/// exception and hashes as-is. Mirrors Besu's <c>BftBlockHashing</c>.
/// </remarks>
public static class BftBlockHashing
{
    private static readonly HeaderDecoder _plainHeaderDecoder = new();
    private static readonly EthereumEcdsa _ecdsa = new(0);

    public static ValueHash256 CalculateOnChainHash(BlockHeader header, IBftExtraDataCodec codec) =>
        header.IsGenesis ? HashAsIs(header) : CalculateOnChainHash(header, codec.Decode(header.ExtraData), codec);

    public static ValueHash256 CalculateOnChainHash(BlockHeader header, BftExtraData extraData, IBftExtraDataCodec codec) =>
        header.IsGenesis ? HashAsIs(header) : HashWithExtraData(header, codec.EncodeWithoutCommitSealsAndRound(extraData));

    public static ValueHash256 CalculateCommitSealDigest(BlockHeader header, IBftExtraDataCodec codec) =>
        CalculateCommitSealDigest(header, codec.Decode(header.ExtraData), codec);

    public static ValueHash256 CalculateCommitSealDigest(BlockHeader header, BftExtraData extraData, IBftExtraDataCodec codec) =>
        header.IsGenesis ? HashAsIs(header) : HashWithExtraData(header, codec.EncodeWithoutCommitSeals(extraData));

    /// <summary>Addresses whose seals are in the extra data, in seal order; a seal that does not recover yields <see cref="Address.Zero"/>.</summary>
    public static Address[] RecoverCommitters(BlockHeader header, BftExtraData extraData, IBftExtraDataCodec codec)
    {
        ValueHash256 digest = CalculateCommitSealDigest(header, extraData, codec);
        IReadOnlyList<Signature> seals = extraData.Seals;
        Address[] committers = new Address[seals.Count];
        for (int i = 0; i < seals.Count; i++)
        {
            committers[i] = _ecdsa.RecoverAddress(seals[i], in digest) ?? Address.Zero;
        }

        return committers;
    }

    /// <summary>Keccak of the header encoded with the standard seal shape and its <c>extraData</c> unchanged.</summary>
    public static ValueHash256 HashAsIs(BlockHeader header)
    {
        KeccakRlpWriter writer = new();
        _plainHeaderDecoder.Encode(ref writer, header);
        return writer.GetValueHash();
    }

    private static ValueHash256 HashWithExtraData(BlockHeader header, byte[] extraData)
    {
        BlockHeader copy = header.Clone();
        copy.ExtraData = extraData;
        return HashAsIs(copy);
    }
}
