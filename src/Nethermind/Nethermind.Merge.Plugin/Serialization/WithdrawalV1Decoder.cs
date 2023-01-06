// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Merge.Plugin.EngineApi.Shanghai.Data;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Merge.Plugin.Serialization;

public class WithdrawalV1Decoder : IRlpStreamDecoder<IWithdrawal?>, IRlpValueDecoder<IWithdrawal?>
{
    public IWithdrawal? Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (rlpStream.IsNextItemNull())
        {
            rlpStream.ReadByte();

            return null;
        }

        rlpStream.ReadSequenceLength();

        return new WithdrawalV1()
        {
            Index = rlpStream.DecodeULong(),
            ValidatorIndex = rlpStream.DecodeULong(),
            Address = rlpStream.DecodeAddress() ?? Address.Zero,
            Amount = rlpStream.DecodeUInt256()
        };
    }

    public IWithdrawal? Decode(ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (decoderContext.IsNextItemNull())
        {
            decoderContext.ReadByte();

            return null;
        }

        decoderContext.ReadSequenceLength();

        return new WithdrawalV1()
        {
            Index = decoderContext.DecodeULong(),
            ValidatorIndex = decoderContext.DecodeULong(),
            Address = decoderContext.DecodeAddress() ?? Address.Zero,
            Amount = decoderContext.DecodeUInt256()
        };
    }

    public void Encode(RlpStream stream, IWithdrawal? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
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
        stream.Encode(item.Amount);
    }

    public Rlp Encode(IWithdrawal? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        RlpStream stream = new(GetLength(item, rlpBehaviors));
        Encode(stream, item, rlpBehaviors);
        return new(stream.Data ?? Bytes.Empty);
    }

    private static int GetContentLength(IWithdrawal? item) =>
        item is null
            ? 0
            : Rlp.LengthOf(item.Index) +
              Rlp.LengthOf(item.ValidatorIndex) +
              Rlp.LengthOfAddressRlp +
              Rlp.LengthOf(item.Amount);

    public int GetLength(IWithdrawal? item, RlpBehaviors _) => Rlp.LengthOfSequence(GetContentLength(item));
}
