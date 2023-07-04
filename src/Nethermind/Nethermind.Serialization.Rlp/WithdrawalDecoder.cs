// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Serialization.Rlp;

public class WithdrawalDecoder : IRlpStreamDecoder<Withdrawal>, IRlpValueDecoder<Withdrawal>
{
    public Withdrawal? Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (rlpStream.IsNextItemNull())
        {
            rlpStream.ReadByte();

            return null;
        }

        rlpStream.ReadSequenceLength();

        return new()
        {
            Index = rlpStream.DecodeULong(),
            ValidatorIndex = rlpStream.DecodeULong(),
            Address = rlpStream.DecodeAddress(),
            AmountInGwei = rlpStream.DecodeULong()
        };
    }

    public Withdrawal? Decode(ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (decoderContext.IsNextItemNull())
        {
            decoderContext.ReadByte();

            return null;
        }

        decoderContext.ReadSequenceLength();

        return new()
        {
            Index = decoderContext.DecodeULong(),
            ValidatorIndex = decoderContext.DecodeULong(),
            Address = decoderContext.DecodeAddress(),
            AmountInGwei = decoderContext.DecodeULong()
        };
    }

    public void Encode(RlpStream stream, Withdrawal? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item is null)
        {
            stream.EncodeNullObject();
            return;
        }

        var contentLength = GetContentLength(item);

        stream.StartSequence(contentLength);
        stream.Encode(item.Index);
        stream.Encode(item.ValidatorIndex);
        stream.Encode(item.Address);
        stream.Encode(item.AmountInGwei);
    }

    public Rlp Encode(Withdrawal? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        var stream = new RlpStream(GetLength(item, rlpBehaviors));

        Encode(stream, item, rlpBehaviors);

        return new(stream.Data);
    }

    private static int GetContentLength(Withdrawal item) =>
        Rlp.LengthOf(item.Index) +
        Rlp.LengthOf(item.ValidatorIndex) +
        Rlp.LengthOfAddressRlp +
        Rlp.LengthOf(item.AmountInGwei);

    public int GetLength(Withdrawal item, RlpBehaviors _) => Rlp.LengthOfSequence(GetContentLength(item));
}
