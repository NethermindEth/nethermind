// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.Types;

namespace Nethermind.Xdc.RLP;

public sealed class VoteDecoder : RlpDecoder<Vote>
{
    private static readonly XdcBlockInfoDecoder _xdcBlockInfoDecoder = new();

    protected override Vote DecodeInternal(ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (decoderContext.IsNextItemEmptyList())
        {
            decoderContext.ReadByte();
            return null;
        }

        int sequenceLength = decoderContext.ReadSequenceLength();
        int endPosition = decoderContext.Position + sequenceLength;

        BlockRoundInfo proposedBlockInfo = _xdcBlockInfoDecoder.Decode(ref decoderContext, rlpBehaviors);
        Signature signature = null;
        if ((rlpBehaviors & RlpBehaviors.ForSealing) != RlpBehaviors.ForSealing)
        {
            signature = decoderContext.DecodeSignature();
        }
        ulong gapNumber = decoderContext.DecodeULong();

        if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) != RlpBehaviors.AllowExtraBytes)
        {
            decoderContext.Check(endPosition);
        }
        return new Vote(proposedBlockInfo, gapNumber, signature);
    }

    public override void PopulateLength(Vote item, ref LengthStack lengths, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item is null)
        {
            lengths.Push(Rlp.OfEmptyList.Length);
            return;
        }

        int contentLength = PopulateContentLength(item, ref lengths, rlpBehaviors);
        lengths.Push(contentLength);
        lengths.Push(Rlp.LengthOfSequence(contentLength));
    }

    public override void Encode<TWriter>(ref TWriter writer, Vote item, ref LengthStack lengths, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item is null)
        {
            writer.EncodeNullObject();
            return;
        }
        writer.StartSequence(lengths.Pop());
        _xdcBlockInfoDecoder.Encode(ref writer, item.ProposedBlockInfo, ref lengths, rlpBehaviors);
        if ((rlpBehaviors & RlpBehaviors.ForSealing) != RlpBehaviors.ForSealing)
        {
            Span<byte> sigBuffer = stackalloc byte[Signature.Size];
            item.Signature.WriteBytesWithRecoveryTo(sigBuffer);
            writer.Encode(sigBuffer);
        }
        writer.Encode(item.GapNumber);
    }

    private int PopulateContentLength(Vote item, ref LengthStack lengths, RlpBehaviors rlpBehaviors) => ((rlpBehaviors & RlpBehaviors.ForSealing) != RlpBehaviors.ForSealing ? Rlp.LengthOfSequence(Signature.Size) : 0)
            + Rlp.LengthOf(item.GapNumber)
            + _xdcBlockInfoDecoder.PopulateAndPop(item.ProposedBlockInfo, ref lengths, rlpBehaviors);
}
