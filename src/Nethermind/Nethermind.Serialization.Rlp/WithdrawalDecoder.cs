// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using System.Diagnostics.CodeAnalysis;

namespace Nethermind.Serialization.Rlp;

[method: DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(WithdrawalDecoder))]
public sealed class WithdrawalDecoder() : RlpDecoder<Withdrawal>
{
    protected override Withdrawal? DecodeInternal(ref ValueRlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (decoderContext.IsNextItemEmptyList())
        {
            decoderContext.ReadByte();
            return null;
        }

        int sequenceLength = decoderContext.ReadSequenceLength();
        int checkPosition = decoderContext.Position + sequenceLength;

        Withdrawal withdrawal = new()
        {
            Index = decoderContext.DecodeULong(),
            ValidatorIndex = decoderContext.DecodeULong(),
            Address = decoderContext.DecodeAddress(),
            AmountInGwei = decoderContext.DecodeULong()
        };

        if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) == 0)
        {
            decoderContext.Check(checkPosition);
        }

        return withdrawal;
    }

    public override void Encode(RlpStream stream, Withdrawal? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        ValueRlpWriter writer = new(stream);
        Encode(ref writer, item, rlpBehaviors);
    }

    public override void Encode(ref ValueRlpWriter writer, Withdrawal? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item is null)
        {
            writer.EncodeNullObject();
            return;
        }

        int contentLength = GetContentLength(item);

        writer.StartSequence(contentLength);
        writer.Encode(item.Index);
        writer.Encode(item.ValidatorIndex);
        writer.Encode(item.Address);
        writer.Encode(item.AmountInGwei);
    }

    public override Rlp Encode(Withdrawal? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        byte[] bytes = new byte[GetLength(item, rlpBehaviors)];
        ValueRlpWriter writer = bytes.AsRlpValueWriter();
        Encode(ref writer, item, rlpBehaviors);
        return new(bytes);
    }

    private static int GetContentLength(Withdrawal item) =>
        Rlp.LengthOf(item.Index) +
        Rlp.LengthOf(item.ValidatorIndex) +
        Rlp.LengthOfAddressRlp +
        Rlp.LengthOf(item.AmountInGwei);

    public override int GetLength(Withdrawal item, RlpBehaviors _) => Rlp.LengthOfSequence(GetContentLength(item));
}
