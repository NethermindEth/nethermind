// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Consensus.AuRa;

/// <summary>
/// AuRa-aware header decoder. Differs from the base <see cref="HeaderDecoder"/> in two places:
/// <list type="bullet">
///   <item>Materialises <see cref="AuRaBlockHeader"/> for the AuRa seal shape (non-32-byte item).</item>
///   <item>Encodes <c>step</c> + <c>signature</c> in place of <c>mixHash</c> + <c>nonce</c> when
///   the header carries an <see cref="IAuRaSealedHeader"/> seal.</item>
/// </list>
/// </summary>
/// <remarks>
/// AuRa chains contain mixed-shape headers: pre-merge headers carry the AuRa seal; post-merge
/// headers carry the Ethash-shape seal. The peek-then-dispatch logic is on the decode side; on
/// the encode side the choice is driven by whether the header instance is <see cref="IAuRaSealedHeader"/>
/// with both seal fields stamped.
/// </remarks>
public sealed class AuRaHeaderDecoder : HeaderDecoder
{
    protected override BlockHeader CreateHeader(
        Hash256? parentHash, Hash256? unclesHash, Address? beneficiary,
        in UInt256 difficulty, long number, long gasLimit, ulong timestamp, byte[] extraData)
        => new AuRaBlockHeader(parentHash, unclesHash, beneficiary, in difficulty, number, gasLimit, timestamp, extraData);

    protected override void DecodeSeal(ref Rlp.ValueDecoderContext decoderContext, BlockHeader header)
    {
        // 32-byte item ⇒ Ethash shape (mixHash + nonce); otherwise AuRa shape (step + signature).
        bool isAuRaShape = decoderContext.PeekPrefixAndContentLength().ContentLength != Hash256.Size;
        if (isAuRaShape && header is IAuRaSealedHeader aura)
        {
            long step = (long)decoderContext.DecodeUInt256();
            byte[]? signature = decoderContext.DecodeByteArray();
            aura.AuRaStep = step;
            aura.AuRaSignature = signature;
        }
        else
        {
            base.DecodeSeal(ref decoderContext, header);
        }
    }

    protected override void EncodeSeal(RlpStream rlpStream, BlockHeader header)
    {
        if (header.TryGetAuRaSeal(out long step, out byte[]? signature))
        {
            rlpStream.Encode(step);
            rlpStream.Encode(signature);
        }
        else
        {
            base.EncodeSeal(rlpStream, header);
        }
    }

    protected override int GetSealContentLength(BlockHeader header)
        => header.TryGetAuRaSeal(out long step, out byte[]? signature)
            ? Rlp.LengthOf(step) + Rlp.LengthOf(signature)
            : base.GetSealContentLength(header);
}
