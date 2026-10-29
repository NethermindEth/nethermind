// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Serialization.FluentRlp;
using System.Diagnostics.CodeAnalysis;

namespace Nethermind.Serialization.Rlp;

[method: DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(WithdrawalDecoder))]
public sealed class WithdrawalDecoder() : RlpDecoder<Withdrawal>
{
    protected override Withdrawal? DecodeInternal(ref RlpReader reader, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        => (rlpBehaviors & RlpBehaviors.TrustedInput) != 0 ? Decode<OffFlag>(ref reader, rlpBehaviors) : Decode<OnFlag>(ref reader, rlpBehaviors);

    private static Withdrawal? Decode<TCanonicalChecks>(ref RlpReader reader, RlpBehaviors rlpBehaviors) where TCanonicalChecks : struct, IFlag
    {
        if (reader.TryReadEmptySequence()) return null;

        RlpReader fields = reader.EnterSequence<TCanonicalChecks>(out int itemLength);
        Withdrawal withdrawal = new()
        {
            Index = fields.DecodeULong<TCanonicalChecks>(),
            ValidatorIndex = fields.DecodeULong<TCanonicalChecks>(),
            Address = fields.DecodeAddress(),
            AmountInGwei = fields.DecodeULong<TCanonicalChecks>()
        };
        reader.ExitSequence(in fields, itemLength, rlpBehaviors);

        return withdrawal;
    }

    public override void Encode(ref RlpWriter writer, Withdrawal? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item is null)
        {
            writer.EncodeNullObject();
            return;
        }

        int contentLength = GetContentLength(item);

        RlpSequence sequence = writer.StartSequence(contentLength);
        writer.Encode(item.Index);
        writer.Encode(item.ValidatorIndex);
        writer.Encode(item.Address);
        writer.Encode(item.AmountInGwei);
        writer.EndSequence(sequence);
    }

    public override Rlp Encode(Withdrawal? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        byte[] bytes = new byte[GetLength(item, rlpBehaviors)];
        RlpWriter writer = RlpWriter.ContentWriter(bytes);
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
