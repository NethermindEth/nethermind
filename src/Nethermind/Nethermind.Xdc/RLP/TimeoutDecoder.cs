// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.Types;

namespace Nethermind.Xdc.RLP;

public class TimeoutDecoder: IRlpValueDecoder<Timeout>, IRlpStreamDecoder<Timeout>
{
    public Timeout Decode(ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (decoderContext.IsNextItemNull())
            return null;
        int sequenceLength = decoderContext.ReadSequenceLength();
        int endPosition = decoderContext.Position + sequenceLength;

        ulong round = decoderContext.DecodeULong();

        Signature signature = null;
        if ((rlpBehaviors & RlpBehaviors.ForSealing) != RlpBehaviors.ForSealing)
        {
            if (decoderContext.PeekNextRlpLength() != Signature.Size)
                throw new RlpException($"Invalid signature length in '{nameof(Timeout)}'");
            signature = new(decoderContext.DecodeByteArray());
        }

        ulong gapNumber = decoderContext.DecodeULong();

        if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) != RlpBehaviors.AllowExtraBytes)
        {
            decoderContext.Check(endPosition);
        }

        return new Timeout(round, signature, gapNumber);
    }

    public Timeout Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (rlpStream.IsNextItemNull())
            return null;
        int sequenceLength = rlpStream.ReadSequenceLength();
        int endPosition = rlpStream.Position + sequenceLength;

        ulong round = rlpStream.DecodeULong();

        Signature signature = null;
        if ((rlpBehaviors & RlpBehaviors.ForSealing) != RlpBehaviors.ForSealing)
        {
            if (rlpStream.PeekNextRlpLength() != Signature.Size)
                throw new RlpException($"Invalid signature length in {nameof(Vote)}");
            signature = new(rlpStream.DecodeByteArray());
        }

        ulong gapNumber = rlpStream.DecodeUlong();

        if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) != RlpBehaviors.AllowExtraBytes)
        {
            rlpStream.Check(endPosition);
        }

        return new Timeout(round, signature, gapNumber);
    }

    public Rlp Encode(Timeout item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item is null)
            return Rlp.OfEmptySequence;

        RlpStream rlpStream = new(GetLength(item, rlpBehaviors));
        Encode(rlpStream, item, rlpBehaviors);

        return new Rlp(rlpStream.Data.ToArray());
    }

    public void Encode(RlpStream stream, Timeout item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item is null)
        {
            stream.EncodeNullObject();
            return;
        }

        stream.StartSequence(GetContentLength(item, rlpBehaviors));

        stream.Encode(item.Round);

        // When encoding for sealing, signature is not included
        if ((rlpBehaviors & RlpBehaviors.ForSealing) != RlpBehaviors.ForSealing)
        {
            if(item.Signature is null)
                stream.EncodeNullObject();
            else
                stream.Encode(item.Signature.BytesWithRecovery);
        }

        stream.Encode(item.GapNumber);
    }

    public int GetLength(Timeout item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        return Rlp.LengthOfSequence(GetContentLength(item, rlpBehaviors));
    }
    private int GetContentLength(Timeout? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
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
