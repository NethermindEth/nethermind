// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Test.Builders;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Core.Test.Encoding;

public class IRlpDecoderTests
{
    [Test]
    public void Decode_complete_not_null_decodes_item()
    {
        IRlpDecoder<Withdrawal> decoder = new WithdrawalDecoder();
        Rlp rlp = decoder.Encode(TestItem.WithdrawalA_1Eth);

        Assert.That(decoder.DecodeCompleteNotNull(rlp.Bytes), Is.Not.Null);
    }

    [Test]
    public void Array_encoding_uses_empty_list_for_null_withdrawals()
    {
        IRlpDecoder<Withdrawal> decoder = new WithdrawalDecoder();
        Withdrawal?[] withdrawals = [TestItem.WithdrawalA_1Eth, null, TestItem.WithdrawalB_2Eth];

        int contentLength = decoder.GetContentLength(withdrawals);

        Assert.That(contentLength, Is.EqualTo(
            decoder.GetLength(withdrawals[0]!, RlpBehaviors.None) +
            Rlp.OfEmptyList.Length +
            decoder.GetLength(withdrawals[2]!, RlpBehaviors.None)));
        Assert.That(decoder.GetLength(withdrawals), Is.EqualTo(Rlp.LengthOfSequence(contentLength)));

        RlpStream stream = new(decoder.GetLength(withdrawals));
        decoder.Encode(stream, withdrawals);
        Rlp.ValueDecoderContext context = new(stream.Data.AsSpan());
        int sequenceEnd = context.ReadSequenceLength() + context.Position;

        Assert.That(decoder.Decode(ref context), Is.Not.Null);
        Assert.That(context.ReadByte(), Is.EqualTo(Rlp.EmptyListByte));
        Assert.That(decoder.Decode(ref context), Is.Not.Null);
        context.Check(sequenceEnd);
    }
}
