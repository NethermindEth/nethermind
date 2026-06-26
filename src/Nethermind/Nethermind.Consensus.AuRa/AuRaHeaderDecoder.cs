// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Consensus.AuRa;

/// <summary>
/// Header decoder for AuRa chains, where the seal section carries <c>step</c> + <c>signature</c>
/// instead of the Ethash <c>mixHash</c> + <c>nonce</c> pair.
/// </summary>
/// <remarks>
/// Post-merge headers keep the Ethash/PoS seal shape, so on merged AuRa chains (e.g. Gnosis) both
/// shapes coexist; a 32-byte seal item disambiguates. Registered by <see cref="AuRaHeaderModule"/>
/// as both the global <see cref="BlockHeader"/> RLP decoder and the DI <see cref="IHeaderDecoder"/>.
/// </remarks>
public sealed class AuRaHeaderDecoder : HeaderDecoder
{
    protected override BlockHeader DecodeSealAndCreateHeader(
        ref RlpReader decoderContext,
        Hash256? parentHash,
        Hash256? unclesHash,
        Address? beneficiary,
        in UInt256 difficulty,
        ulong number,
        ulong gasLimit,
        ulong timestamp,
        byte[] extraData)
    {
        if (decoderContext.PeekPrefixAndContentLength().ContentLength == Hash256.Size)
        {
            return base.DecodeSealAndCreateHeader(
                ref decoderContext, parentHash, unclesHash, beneficiary, in difficulty, number, gasLimit, timestamp, extraData);
        }

        ulong step = decoderContext.DecodeULong();
        byte[] signature = decoderContext.DecodeByteArray();
        return new AuRaBlockHeader(parentHash, unclesHash, beneficiary, in difficulty, number, gasLimit, timestamp, extraData)
        {
            AuRaStep = step,
            AuRaSignature = signature,
        };
    }

    /// <remarks>
    /// Between <c>AuRaBlockProducer.PrepareBlock</c> (stamps step) and <c>AuRaSealer.SealBlock</c>
    /// (stamps signature) an <see cref="AuRaBlockHeader"/> has a null <c>AuRaSignature</c>, which would
    /// encode as an empty byte string. This is safe because such unsealed headers are never persisted
    /// or sent over P2P — encoding only ever runs after the seal is complete (or on the
    /// <see cref="RlpBehaviors.ForSealing"/> path, which omits the seal section entirely).
    /// </remarks>
    protected override void EncodeSeal<TWriter>(ref TWriter writer, BlockHeader header)
    {
        if (header is AuRaBlockHeader aura)
        {
            writer.Encode(aura.AuRaStep);
            writer.Encode(aura.AuRaSignature);
        }
        else
        {
            base.EncodeSeal(ref writer, header);
        }
    }

    protected override int GetSealLength(BlockHeader header) =>
        header is AuRaBlockHeader aura
            ? Rlp.LengthOf(aura.AuRaStep) + Rlp.LengthOf(aura.AuRaSignature)
            : base.GetSealLength(header);
}
