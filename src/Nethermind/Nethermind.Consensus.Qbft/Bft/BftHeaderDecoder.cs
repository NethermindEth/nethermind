// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Consensus.Qbft.Bft;

/// <summary>
/// Header decoder for QBFT chains: materialises <see cref="BftBlockHeader"/> instances and sets
/// their hash to the BFT on-chain digest rather than the keccak of the received RLP.
/// </summary>
/// <remarks>
/// The seal shape is the standard one, so encoding is inherited unchanged. Registered by
/// <see cref="QbftModule"/> both as the global <see cref="BlockHeader"/> RLP decoder and the DI
/// <see cref="IHeaderDecoder"/>.
/// </remarks>
public sealed class BftHeaderDecoder(IBftExtraDataCodecSelector codecs) : HeaderDecoder
{
    public BftHeaderDecoder() : this(QbftOnlyCodecSelector.Instance) { }

    protected override BlockHeader DecodeSealAndCreateHeader(
        ref RlpReader decoderContext,
        Hash256 parentHash,
        Hash256 unclesHash,
        Address beneficiary,
        in UInt256 difficulty,
        ulong number,
        ulong gasLimit,
        ulong timestamp,
        byte[] extraData)
    {
        Hash256 mixHash = decoderContext.DecodeKeccak();
        ulong nonce = (ulong)decoderContext.DecodeUInt256(NonceLength);
        return new BftBlockHeader(parentHash, unclesHash, beneficiary, in difficulty, number, gasLimit, timestamp, extraData)
        {
            MixHash = mixHash,
            Nonce = nonce,
            Codec = codecs.ForBlock(number),
        };
    }

    protected override BlockHeader? DecodeInternal(ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        BlockHeader? header = base.DecodeInternal(ref decoderContext, rlpBehaviors);
        // The base decoder hashes the raw bytes before the optional tail is read; the BFT digest needs the whole header.
        if (header is BftBlockHeader qbftHeader && !qbftHeader.IsGenesis)
        {
            qbftHeader.Hash = new Hash256(qbftHeader.CalculateHash());
        }

        return header;
    }
}
