// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Taiko;

public class L1OriginDecoder : IRlpStreamDecoder<L1Origin>
{
    public L1Origin Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        (int _, int contentLength) = rlpStream.ReadPrefixAndContentLength();

        int itemsCount = rlpStream.PeekNumberOfItemsRemaining(maxSearch: contentLength);

        UInt256 blockId = rlpStream.DecodeUInt256();
        Hash256? l2BlockHash = rlpStream.DecodeKeccak();
        var l1BlockHeight = rlpStream.DecodeLong();
        Hash256 l1BlockHash = rlpStream.DecodeKeccak() ?? throw new RlpException("L1BlockHash is null");
        byte[]? buildPayloadArgsId = itemsCount == 4 ? null : rlpStream.DecodeByteArray();

        return new(blockId, l2BlockHash, l1BlockHeight, l1BlockHash, buildPayloadArgsId);
    }

    public Rlp Encode(L1Origin? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item is null)
            return Rlp.OfEmptySequence;

        RlpStream rlpStream = new(GetLength(item, rlpBehaviors));
        Encode(rlpStream, item, rlpBehaviors);
        return new(rlpStream.Data.ToArray()!);
    }

    public void Encode(RlpStream stream, L1Origin item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        stream.StartSequence(GetLength(item, rlpBehaviors));

        stream.Encode(item.BlockId);
        stream.Encode(item.L2BlockHash);
        stream.Encode(item.L1BlockHeight);
        stream.Encode(item.L1BlockHash);
        if (item.BuildPayloadArgsId is not null)
        {
            stream.Encode(item.BuildPayloadArgsId);

        }
    }

    public int GetLength(L1Origin item, RlpBehaviors rlpBehaviors)
    {
        return Rlp.LengthOfSequence(
            Rlp.LengthOf(item.BlockId)
            + Rlp.LengthOf(item.L2BlockHash)
            + Rlp.LengthOf(item.L1BlockHeight)
            + Rlp.LengthOf(item.L1BlockHash)
            + (item.BuildPayloadArgsId is null ? 0 : Rlp.LengthOf(item.BuildPayloadArgsId))
        );
    }
}
