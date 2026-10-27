// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.Types;

namespace Nethermind.Xdc.RLP;

internal sealed class XdcBlockInfoDecoder : RlpDecoder<BlockRoundInfo>
{
    protected override BlockRoundInfo DecodeInternal(ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (decoderContext.IsNextItemEmptyList())
        {
            decoderContext.ReadByte();
            return null;
        }
        int sequenceLength = decoderContext.ReadSequenceLength();
        int endPosition = decoderContext.Position + sequenceLength;

        byte[] hashBytes = decoderContext.DecodeByteArray();
        if (hashBytes.Length > Hash256.Size)
            throw new RlpException($"Hash length {hashBytes.Length} is longer than max size of 32.");
        ulong round = decoderContext.DecodeULong();
        ulong number = decoderContext.DecodeULong();

        return new BlockRoundInfo(new Hash256(hashBytes), round, number);
    }

    public override void PopulateLength(BlockRoundInfo item, ref LengthStack lengths, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item is null)
        {
            lengths.Push(Rlp.OfEmptyList.Length);
            return;
        }

        int contentLength = GetContentLength(item, rlpBehaviors);
        lengths.Push(contentLength);
        lengths.Push(Rlp.LengthOfSequence(contentLength));
    }

    public override void Encode<TWriter>(BlockRoundInfo item, ref LengthStack lengths, ref TWriter writer, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item is null)
        {
            writer.EncodeNullObject();
            return;
        }
        writer.StartSequence(lengths.Pop());
        writer.Encode(item.Hash);
        writer.Encode(item.Round);
        writer.Encode(item.BlockNumber);
    }

    private static int GetContentLength(BlockRoundInfo? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item is null)
            return 0;
        return Rlp.LengthOf(item.Hash) +
               Rlp.LengthOf(item.Round) +
               Rlp.LengthOf(item.BlockNumber);
    }

}
