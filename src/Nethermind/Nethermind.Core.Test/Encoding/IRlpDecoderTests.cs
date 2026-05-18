// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Collections;
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

    [Test]
    public void Decode_wraps_out_of_range_with_concrete_type_name()
    {
        RlpException? exception = Assert.Throws<RlpException>(DecodeEmptyInput);

        Assert.That(exception!.Message, Does.Contain(nameof(Withdrawal)));
        Assert.That(exception.Message, Does.Not.Contain(" T"));

        static void DecodeEmptyInput()
        {
            IRlpDecoder<Withdrawal> decoder = new WithdrawalDecoder();
            Rlp.ValueDecoderContext context = new(ReadOnlySpan<byte>.Empty);
            decoder.Decode(ref context);
        }
    }

    [Test]
    public void Netty_array_encoding_uses_empty_list_for_null_withdrawals()
    {
        IRlpDecoder<Withdrawal> decoder = new WithdrawalDecoder();
        Withdrawal?[] withdrawals = [TestItem.WithdrawalA_1Eth, null, TestItem.WithdrawalB_2Eth];

        using NettyRlpStream stream = decoder.EncodeToNewNettyStream(withdrawals);

        AssertEncodedNullItem(decoder, stream.AsSpan());
    }

    [Test]
    public void Netty_list_encoding_uses_empty_list_for_null_withdrawals()
    {
        IRlpDecoder<Withdrawal> decoder = new WithdrawalDecoder();
        List<Withdrawal?> withdrawals = [TestItem.WithdrawalA_1Eth, null, TestItem.WithdrawalB_2Eth];

        using NettyRlpStream stream = decoder.EncodeToNewNettyStream(withdrawals);

        AssertEncodedNullItem(decoder, stream.AsSpan());
    }

    [Test]
    public void Netty_array_pool_list_ref_encoding_uses_empty_list_for_null_withdrawals()
    {
        IRlpDecoder<Withdrawal> decoder = new WithdrawalDecoder();
        using ArrayPoolListRef<Withdrawal?> withdrawals = new(3);
        withdrawals.Add(TestItem.WithdrawalA_1Eth);
        withdrawals.Add(null);
        withdrawals.Add(TestItem.WithdrawalB_2Eth);

        using NettyRlpStream stream = decoder.EncodeToNewNettyStream(in withdrawals);

        AssertEncodedNullItem(decoder, stream.AsSpan());
    }

    private static void AssertEncodedNullItem(IRlpDecoder<Withdrawal> decoder, ReadOnlySpan<byte> bytes)
    {
        Rlp.ValueDecoderContext context = new(bytes);
        int sequenceEnd = context.ReadSequenceLength() + context.Position;

        Assert.That(decoder.Decode(ref context), Is.Not.Null);
        Assert.That(context.ReadByte(), Is.EqualTo(Rlp.EmptyListByte));
        Assert.That(decoder.Decode(ref context), Is.Not.Null);
        context.Check(sequenceEnd);
    }
}
