// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.ConsensusRequests;

namespace Nethermind.Serialization.Rlp;

public class DepositDecoder : IRlpStreamDecoder<Deposit>, IRlpValueDecoder<Deposit>
{
    public Deposit? Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (rlpStream.IsNextItemNull())
        {
            rlpStream.ReadByte();

            return null;
        }

        // rlpStream.ReadSequenceLength();

        return new()
        {
            PubKey = rlpStream.DecodeByteArray(),
            WithdrawalCredentials = rlpStream.DecodeByteArray(),
            Amount = rlpStream.DecodeULong(),
            Signature = rlpStream.DecodeByteArray(),
            Index = rlpStream.DecodeULong(),
        };
    }

    public Deposit? Decode(ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (decoderContext.IsNextItemNull())
        {
            decoderContext.ReadByte();

            return null;
        }

        // decoderContext.ReadSequenceLength();

        return new()
        {
            PubKey = decoderContext.DecodeByteArray(),
            WithdrawalCredentials = decoderContext.DecodeByteArray(),
            Amount = decoderContext.DecodeULong(),
            Signature = decoderContext.DecodeByteArray(),
            Index = decoderContext.DecodeULong(),
        };
    }

    public void Encode(RlpStream stream, Deposit? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item is null)
        {
            stream.EncodeNullObject();
            return;
        }

        // var contentLength = GetContentLength(item);

        // stream.StartSequence(contentLength);
        stream.Encode(item.PubKey);
        stream.Encode(item.WithdrawalCredentials);
        stream.Encode(item.Amount);
        stream.Encode(item.Signature);
        stream.Encode(item.Index.Value);
    }

    public Rlp Encode(Deposit? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        var stream = new RlpStream(GetLength(item, rlpBehaviors));

        Encode(stream, item, rlpBehaviors);

        return new(stream.Data.ToArray());
    }

    public int GetContentLength(Deposit item, RlpBehaviors rlpBehaviors) =>
        Rlp.LengthOf(item.PubKey) +
        Rlp.LengthOf(item.WithdrawalCredentials) +
        Rlp.LengthOf(item.Amount) +
        Rlp.LengthOf(item.Signature) +
        Rlp.LengthOf(item.Index);

    public int GetLength(Deposit item, RlpBehaviors rlpBehaviors) => Rlp.LengthOfSequence(GetContentLength(item, rlpBehaviors));
}
