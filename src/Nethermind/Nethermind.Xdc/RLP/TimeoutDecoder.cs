// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.Types;

namespace Nethermind.Xdc.RLP;

public sealed class TimeoutDecoder : RlpDecoder<Timeout>
{
    protected override Timeout DecodeInternal(ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (decoderContext.IsNextItemEmptyList())
        {
            decoderContext.ReadByte();
            return null;
        }
        int sequenceLength = decoderContext.ReadSequenceLength();
        int endPosition = decoderContext.Position + sequenceLength;

        ulong round = decoderContext.DecodeULong();

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

        return new Timeout(round, signature, gapNumber);
    }

    public override void Encode<TWriter>(ref TWriter writer, Timeout item, ref LengthList lengths, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item is null)
        {
            writer.EncodeNullObject();
            return;
        }

        writer.StartSequence(lengths.Read());

        writer.Encode(item.Round);

        // When encoding for sealing, signature is not included
        if ((rlpBehaviors & RlpBehaviors.ForSealing) != RlpBehaviors.ForSealing)
        {
            if (item.Signature is null)
                writer.EncodeNullObject();
            else
            {
                Span<byte> sigBuffer = stackalloc byte[Signature.Size];
                item.Signature.WriteBytesWithRecoveryTo(sigBuffer);
                writer.Encode(sigBuffer);
            }
        }

        writer.Encode(item.GapNumber);
    }

    public override void PopulateLength(Timeout item, in LengthListWriter lengths, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item is null)
        {
            lengths.Add(Rlp.OfEmptyList.Length);
            return;
        }

        using LengthListWriter sequence = lengths.BeginContainer();
        sequence.Add(GetContentLength(item, rlpBehaviors));
    }

    public int GetContentLength(Timeout? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item is null)
            return 0;
        int contentLength = Rlp.LengthOf(item.Round);
        if ((rlpBehaviors & RlpBehaviors.ForSealing) != RlpBehaviors.ForSealing)
        {
            contentLength += Rlp.LengthOfSequence(Signature.Size);
        }
        contentLength += Rlp.LengthOf(item.GapNumber);
        return contentLength;
    }
}
