// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using System.Diagnostics.CodeAnalysis;

namespace Nethermind.Serialization.Rlp;

[method: DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(WithdrawalDecoder))]
public sealed class WithdrawalDecoder() : RlpDecoder<Withdrawal>
{
    protected override Withdrawal? DecodeInternal(ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (decoderContext.TryConsumeNull(out LiteRlpReader rlp, out int position)) return null;

        rlp.ReadSequenceLength(ref position, out int sequenceLength);
        int checkPosition = position + sequenceLength;

        rlp.DecodeULong(ref position, out ulong index);
        rlp.DecodeULong(ref position, out ulong validatorIndex);
        rlp.DecodeAddress(ref position, out Address address);
        rlp.DecodeULong(ref position, out ulong amountInGwei);
        decoderContext.Position = position;

        Withdrawal withdrawal = new()
        {
            Index = index,
            ValidatorIndex = validatorIndex,
            Address = address,
            AmountInGwei = amountInGwei
        };

        if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) == 0)
        {
            RlpHelpers.Check(position, checkPosition);
        }

        return withdrawal;
    }

    public override void Encode<TWriter>(ref TWriter writer, Withdrawal? item, ref LengthStack lengths, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item is null)
        {
            writer.EncodeNullObject();
            return;
        }

        writer.StartSequence(lengths.Pop());
        writer.Encode(item.Index);
        writer.Encode(item.ValidatorIndex);
        writer.Encode(item.Address);
        writer.Encode(item.AmountInGwei);
    }

    public override void PopulateLength(Withdrawal? item, ref LengthStack lengths, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item is null)
        {
            lengths.Push(Rlp.OfEmptyList.Length);
            return;
        }

        int contentLength = Rlp.LengthOf(item.Index) +
            Rlp.LengthOf(item.ValidatorIndex) +
            Rlp.LengthOfAddressRlp +
            Rlp.LengthOf(item.AmountInGwei);
        lengths.Push(contentLength);
        lengths.Push(Rlp.LengthOfSequence(contentLength));
    }
}
