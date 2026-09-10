// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Consensus.Qbft.Bft;

/// <summary>Which parts of the extra data are written; the shorter forms feed the two consensus digests.</summary>
public enum BftExtraDataEncoding
{
    /// <summary>Everything, as stored on chain.</summary>
    All,
    /// <summary>Round kept, seals emptied: the digest each validator signs to produce its committed seal.</summary>
    ExcludeCommitSeals,
    /// <summary>Round zeroed and seals emptied: the digest that is the block hash.</summary>
    ExcludeCommitSealsAndRound
}

/// <summary>
/// RLP codec for the BFT consensus payload carried in a block header's <c>extraData</c>.
/// </summary>
/// <remarks>
/// QBFT and IBFT 2.0 share the same fields but differ in how an absent vote, the round and the
/// omitted seals are encoded, so a migrated chain needs one codec per era.
/// </remarks>
public interface IBftExtraDataCodec
{
    byte[] Encode(BftExtraData extraData, BftExtraDataEncoding encoding = BftExtraDataEncoding.All);

    /// <exception cref="Serialization.Rlp.RlpException">The bytes are not a well-formed BFT extra data structure.</exception>
    BftExtraData Decode(ReadOnlySpan<byte> extraData);
}

public static class BftExtraDataCodecExtensions
{
    extension(IBftExtraDataCodec codec)
    {
        public byte[] EncodeWithoutCommitSeals(BftExtraData extraData) => codec.Encode(extraData, BftExtraDataEncoding.ExcludeCommitSeals);

        public byte[] EncodeWithoutCommitSealsAndRound(BftExtraData extraData) => codec.Encode(extraData, BftExtraDataEncoding.ExcludeCommitSealsAndRound);

        public bool TryDecode(ReadOnlySpan<byte> extraData, out BftExtraData? decoded)
        {
            try
            {
                decoded = codec.Decode(extraData);
                return true;
            }
            catch (Exception e) when (e is Serialization.Rlp.RlpException or ArgumentException or IndexOutOfRangeException)
            {
                decoded = null;
                return false;
            }
        }
    }
}
