// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using System;

namespace Nethermind.Taiko;

public sealed class L1OriginDecoder : RlpStreamDecoder<L1Origin>
{
    const int BuildPayloadArgsIdLength = 8;
    const int SignatureLength = 65;

    protected override L1Origin DecodeInternal(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        (int _, int contentLength) = rlpStream.ReadPrefixAndContentLength();
        int itemsCount = rlpStream.PeekNumberOfItemsRemaining(maxSearch: contentLength);

        UInt256 blockId = rlpStream.DecodeUInt256();
        Hash256? l2BlockHash = rlpStream.DecodeKeccak();
        long? l1BlockHeight = rlpStream.DecodeLong();
        Hash256 l1BlockHash = rlpStream.DecodeKeccak() ?? throw new RlpException("L1BlockHash is null");

        int[]? buildPayloadArgsId = null;

        if (itemsCount >= 5)
        {
            byte[] buildPayloadBytes = rlpStream.DecodeByteArray();
            buildPayloadArgsId = buildPayloadBytes.Length > 0 ? Array.ConvertAll(buildPayloadBytes, Convert.ToInt32) : null;
        }

        bool isForcedInclusion = itemsCount >= 6 && rlpStream.DecodeBool();
        int[]? signature = itemsCount >= 7 ? Array.ConvertAll(rlpStream.DecodeByteArray(), Convert.ToInt32) : null;

        return new(blockId, l2BlockHash, l1BlockHeight, l1BlockHash, buildPayloadArgsId, isForcedInclusion, signature);
    }

    public Rlp Encode(L1Origin? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item is null)
            return Rlp.OfEmptySequence;

        RlpStream rlpStream = new(GetLength(item, rlpBehaviors));
        Encode(rlpStream, item, rlpBehaviors);
        return new(rlpStream.Data.ToArray()!);
    }

    public override void Encode(RlpStream stream, L1Origin item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        stream.StartSequence(GetLength(item, rlpBehaviors));

        stream.Encode(item.BlockId);
        stream.Encode(item.L2BlockHash);
        stream.Encode(item.L1BlockHeight ?? 0);
        stream.Encode(item.L1BlockHash);

        // If all optional remaining fields are missing, nothing to encode
        if (item.BuildPayloadArgsId is null && !item.IsForcedInclusion && item.Signature is null)
            return;

        // Encode buildPayloadArgsId, even if empty, to maintain field order
        if (item.BuildPayloadArgsId is not null)
        {
            if (item.BuildPayloadArgsId.Length is not BuildPayloadArgsIdLength)
            {
                throw new RlpException($"{nameof(item.BuildPayloadArgsId)} should be exactly {BuildPayloadArgsIdLength}");
            }
            stream.Encode(Array.ConvertAll(item.BuildPayloadArgsId, Convert.ToByte));
        }
        else
        {
            stream.Encode(Array.Empty<byte>());
        }

        // If neither IsForcedInclusion nor Signature are present, return
        if (!item.IsForcedInclusion && item.Signature is null)
            return;

        stream.Encode(item.IsForcedInclusion);

        if (item.Signature is not null)
        {
            if (item.Signature.Length != SignatureLength)
            {
                throw new RlpException($"{nameof(item.Signature)} should be exactly {SignatureLength}");
            }

            stream.Encode(Array.ConvertAll(item.Signature, Convert.ToByte));
        }
    }

    public override int GetLength(L1Origin item, RlpBehaviors rlpBehaviors)
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

        return Rlp.LengthOfSequence(
            Rlp.LengthOf(item.BlockId)
            + Rlp.LengthOf(item.L2BlockHash)
            + Rlp.LengthOf(item.L1BlockHeight ?? 0)
            + Rlp.LengthOf(item.L1BlockHash)
            + buildPayloadLength
            + isForcedInclusionLength
            + (item.Signature is null ? 0 : Rlp.LengthOfByteString(SignatureLength, 0))
        );
    }
}
