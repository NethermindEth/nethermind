// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;
using System;

namespace Nethermind.Taiko;

public sealed class L1OriginDecoder : RlpDecoder<L1Origin>
{
    const int BuildPayloadArgsIdLength = 8;
    internal const int SignatureLength = 65;

    protected override L1Origin? DecodeInternal(ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        => (rlpBehaviors & RlpBehaviors.TrustedInput) != 0
            ? Decode<OffFlag>(ref decoderContext, rlpBehaviors)
            : Decode<OnFlag>(ref decoderContext, rlpBehaviors);

    private static L1Origin? Decode<TCanonicalChecks>(ref RlpReader decoderContext, RlpBehaviors rlpBehaviors) where TCanonicalChecks : struct, IFlag
    {
        if (decoderContext.TryReadEmptySequence())
        {
            return null;
        }

        RlpReader fields = decoderContext.EnterSequence<TCanonicalChecks>(out int itemLength);

        UInt256 blockId = fields.DecodeUInt256<TCanonicalChecks>();
        Hash256? l2BlockHash = fields.DecodeKeccakOrNull();
        long? l1BlockHeight = fields.DecodeLong<TCanonicalChecks>();
        Hash256 l1BlockHash = fields.DecodeKeccak();

        int[]? buildPayloadArgsId = null;

        if (fields.HasNext)
        {
            byte[] buildPayloadBytes = fields.DecodeByteArray<TCanonicalChecks>();
            buildPayloadArgsId = buildPayloadBytes.Length > 0 ? Array.ConvertAll(buildPayloadBytes, Convert.ToInt32) : null;
        }

        bool isForcedInclusion = fields.HasNext && fields.DecodeBool();
        byte[]? signature = fields.HasNext ? fields.DecodeByteArray<TCanonicalChecks>() : null;

        decoderContext.ExitSequence(in fields, itemLength, rlpBehaviors);
        return new(blockId, l2BlockHash, l1BlockHeight, l1BlockHash, buildPayloadArgsId, isForcedInclusion, signature);
    }

    public override void Encode(ref RlpWriter writer, L1Origin item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        RlpSequence sequence = writer.StartSequence(GetContentLength(item, rlpBehaviors));
        EncodeFields(ref writer, item);
        writer.EndSequence(sequence);
    }

    private static void EncodeFields(ref RlpWriter writer, L1Origin item)
    {
        writer.Encode(item.BlockId);
        writer.Encode(item.L2BlockHash);
        writer.Encode(item.L1BlockHeight ?? 0);
        writer.Encode(item.L1BlockHash);

        // If all optional remaining fields are missing, nothing to encode
        if (item.BuildPayloadArgsId is null && !item.IsForcedInclusion && item.Signature is null)
            return;

        // Encode buildPayloadArgsId, even if empty, to maintain field order
        if (item.BuildPayloadArgsId is not null)
        {
            if (item.BuildPayloadArgsId.Length is not BuildPayloadArgsIdLength)
            {
                throw new RlpReaderException($"{nameof(item.BuildPayloadArgsId)} should be exactly {BuildPayloadArgsIdLength}");
            }

            Span<byte> buildPayloadArgsId = stackalloc byte[BuildPayloadArgsIdLength];
            for (int i = 0; i < BuildPayloadArgsIdLength; i++)
            {
                buildPayloadArgsId[i] = Convert.ToByte(item.BuildPayloadArgsId[i]);
            }

            writer.Encode(buildPayloadArgsId);
        }
        else
        {
            writer.Encode(Array.Empty<byte>());
        }

        // If neither IsForcedInclusion nor Signature are present, return
        if (!item.IsForcedInclusion && item.Signature is null)
            return;

        writer.Encode(item.IsForcedInclusion);

        if (item.Signature is not null)
        {
            if (item.Signature.Length != SignatureLength)
            {
                throw new RlpReaderException($"{nameof(item.Signature)} should be exactly {SignatureLength}");
            }

            writer.Encode(item.Signature);
        }
    }

    public override int GetLength(L1Origin? item, RlpBehaviors rlpBehaviors) =>
        item is null ? 1 : Rlp.LengthOfSequence(GetContentLength(item, rlpBehaviors));

    private int GetContentLength(L1Origin item, RlpBehaviors rlpBehaviors)
    {
        int buildPayloadLength = 0;
        if (item.BuildPayloadArgsId is not null || item.IsForcedInclusion || item.Signature is not null)
        {
            buildPayloadLength = item.BuildPayloadArgsId is null
                ? Rlp.LengthOf(Array.Empty<byte>())
                : Rlp.LengthOfByteString(BuildPayloadArgsIdLength, 0);
        }

        int isForcedInclusionLength = 0;
        if (item.IsForcedInclusion || item.Signature is not null)
        {
            isForcedInclusionLength = Rlp.LengthOf(item.IsForcedInclusion);
        }

        return Rlp.LengthOf(item.BlockId)
            + Rlp.LengthOf(item.L2BlockHash)
            + Rlp.LengthOf(item.L1BlockHeight ?? 0)
            + Rlp.LengthOf(item.L1BlockHash)
            + buildPayloadLength
            + isForcedInclusionLength
            + (item.Signature is null ? 0 : Rlp.LengthOfByteString(SignatureLength, 0));
    }
}
