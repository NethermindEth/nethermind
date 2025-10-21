// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.RLP;
using Nethermind.Xdc.Types;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Xdc;
public class VoteDecoder : IRlpValueDecoder<Vote>, IRlpStreamDecoder<Vote>
{
    private static readonly XdcBlockInfoDecoder _xdcBlockInfoDecoder = new();

    public Vote Decode(ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (decoderContext.IsNextItemNull())
            return null;
        int sequenceLength = decoderContext.ReadSequenceLength();
        int endPosition = decoderContext.Position + sequenceLength;

        BlockRoundInfo proposedBlockInfo = _xdcBlockInfoDecoder.Decode(ref decoderContext, rlpBehaviors);
        Signature signature = null;
        if ((rlpBehaviors & RlpBehaviors.ForSealing) != RlpBehaviors.ForSealing)
        {
            if (decoderContext.PeekNextRlpLength() != Signature.Size)
                throw new RlpException($"Invalid signature length in '{nameof(Vote)}'");
            signature = new(decoderContext.DecodeByteArray());
        }
        ulong gapNumber = decoderContext.DecodeULong();

        if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) != RlpBehaviors.AllowExtraBytes)
        {
            decoderContext.Check(endPosition);
        }
        return new Vote(proposedBlockInfo, gapNumber, signature);
    }

    public Vote Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (rlpStream.IsNextItemNull())
            return null;
        int sequenceLength = rlpStream.ReadSequenceLength();
        int endPosition = rlpStream.Position + sequenceLength;

        BlockRoundInfo proposedBlockInfo = _xdcBlockInfoDecoder.Decode(rlpStream, rlpBehaviors);
        Signature signature = null;
        if ((rlpBehaviors & RlpBehaviors.ForSealing) != RlpBehaviors.ForSealing)
        {
            if (rlpStream.PeekNextRlpLength() != Signature.Size)
                throw new RlpException($"Invalid signature length in {nameof(Vote)}");
            signature = new(rlpStream.DecodeByteArray());
        }
        ulong gapNumber = rlpStream.DecodeULong();

        if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) != RlpBehaviors.AllowExtraBytes)
        {
            rlpStream.Check(endPosition);
        }
        return new Vote(proposedBlockInfo, gapNumber, signature);
    }

    public void Encode(RlpStream stream, Vote item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item is null)
        {
            stream.EncodeNullObject();
            return;
        }
        stream.StartSequence(GetContentLength(item, rlpBehaviors));
        _xdcBlockInfoDecoder.Encode(stream, item.ProposedBlockInfo, rlpBehaviors);
        if ((rlpBehaviors & RlpBehaviors.ForSealing) != RlpBehaviors.ForSealing)
            stream.Encode(item.Signature.BytesWithRecovery);
        stream.Encode(item.GapNumber);
    }

    public Rlp Encode(Vote item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item is null)
            return Rlp.OfEmptySequence;

        RlpStream rlpStream = new(GetLength(item, rlpBehaviors));
        Encode(rlpStream, item, rlpBehaviors);

        return new Rlp(rlpStream.Data.ToArray());
    }

    public int GetLength(Vote item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        return Rlp.LengthOfSequence(GetContentLength(item, rlpBehaviors));
    }

    private int GetContentLength(Vote item, RlpBehaviors rlpBehaviors)
    {
        return
            (rlpBehaviors & RlpBehaviors.ForSealing) != RlpBehaviors.ForSealing ? Rlp.LengthOfSequence(Signature.Size) : 0
            + Rlp.LengthOf(item.GapNumber)
            + _xdcBlockInfoDecoder.GetLength(item.ProposedBlockInfo, rlpBehaviors);
    }
}
