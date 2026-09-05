// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using System.Diagnostics.CodeAnalysis;

namespace Nethermind.Serialization.Rlp;

[method: DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(WithdrawalDecoder))]
public sealed class WithdrawalDecoder() : RlpDecoder<Withdrawal>
{
    protected override Withdrawal? DecodeInternal(ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        ReadOnlySpan<byte> rlp = decoderContext.Data;
        int position = decoderContext.Position;

        if (rlp[position] == Rlp.EmptyListByte)
        {
            decoderContext.Position = position + 1;
            return null;
        }

        position = RlpHelpers.ReadSequenceLength(rlp, position, out int sequenceLength);
        int checkPosition = position + sequenceLength;

        position = RlpHelpers.DecodeULong(rlp, position, out ulong index);
        position = RlpHelpers.DecodeULong(rlp, position, out ulong validatorIndex);
        position = RlpHelpers.DecodeAddress(rlp, position, allowNull: false, out Address? address);
        position = RlpHelpers.DecodeULong(rlp, position, out ulong amountInGwei);
        decoderContext.Position = position;

        Withdrawal withdrawal = new()
        {
            Index = index,
            ValidatorIndex = validatorIndex,
            Address = address!,
            AmountInGwei = amountInGwei
        };

        if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) == 0)
        {
            decoderContext.Check(checkPosition);
        }

        return withdrawal;
    }

    public override void Encode<TWriter>(ref TWriter writer, Withdrawal? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
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
        RlpWriter writer = new(bytes);
        Encode(ref writer, item, rlpBehaviors);
        return new(bytes);
    }

    private static int GetContentLength(Withdrawal item) =>
        Rlp.LengthOf(item.Index) +
        Rlp.LengthOf(item.ValidatorIndex) +
        Rlp.LengthOfAddressRlp +
        Rlp.LengthOf(item.AmountInGwei);

    public override int GetLength(Withdrawal? item, RlpBehaviors _)
        => item is null ? Rlp.OfEmptyList.Length : Rlp.LengthOfSequence(GetContentLength(item));
}
