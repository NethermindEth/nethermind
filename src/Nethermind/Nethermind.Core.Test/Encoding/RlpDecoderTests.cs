// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Collections;
using Nethermind.Core.Test.Builders;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Core.Test.Encoding;

public class RlpDecoderTests
{
    [Test]
    public void Decode_complete_not_null_decodes_item()
    {
        WithdrawalDecoder decoder = new();
        Rlp rlp = decoder.Encode(TestItem.WithdrawalA_1Eth);

        Assert.That(decoder.DecodeCompleteNotNull(rlp.Bytes), Is.Not.Null);
    }

    [Test]
    public void Array_encoding_uses_empty_list_for_null_withdrawals()
    {
        WithdrawalDecoder decoder = new();
        Withdrawal?[] withdrawals = [TestItem.WithdrawalA_1Eth, null, TestItem.WithdrawalB_2Eth];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoder.GetContentLength((Withdrawal?[]?)null), Is.Zero);
            Assert.That(decoder.GetLength((Withdrawal?[]?)null), Is.EqualTo(Rlp.OfEmptyList.Length));
        }

        int contentLength = decoder.GetContentLength(withdrawals);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(contentLength, Is.EqualTo(
                decoder.GetLength(withdrawals[0]!, RlpBehaviors.None) +
                Rlp.OfEmptyList.Length +
                decoder.GetLength(withdrawals[2]!, RlpBehaviors.None)));
            Assert.That(decoder.GetLength(withdrawals), Is.EqualTo(Rlp.LengthOfSequence(contentLength)));
        }

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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exception!.Message, Does.Contain(nameof(Withdrawal)));
            Assert.That(exception.Message, Does.Not.Contain(" T"));
        }

        static void DecodeEmptyInput()
        {
            WithdrawalDecoder decoder = new();
            Rlp.ValueDecoderContext context = new(ReadOnlySpan<byte>.Empty);
            decoder.Decode(ref context);
        }
    }

    [Test]
    public void Netty_array_encoding_uses_empty_list_for_null_withdrawals()
    {
        WithdrawalDecoder decoder = new();
        Withdrawal?[] withdrawals = [TestItem.WithdrawalA_1Eth, null, TestItem.WithdrawalB_2Eth];

        using NettyRlpStream stream = decoder.EncodeToNewNettyStream(withdrawals);

        AssertEncodedNullItem(decoder, stream.AsSpan());
    }

    [Test]
    public void Netty_list_encoding_uses_empty_list_for_null_withdrawals()
    {
        WithdrawalDecoder decoder = new();
        List<Withdrawal?> withdrawals = [TestItem.WithdrawalA_1Eth, null, TestItem.WithdrawalB_2Eth];

        using NettyRlpStream stream = decoder.EncodeToNewNettyStream(withdrawals);

        AssertEncodedNullItem(decoder, stream.AsSpan());
    }

    [Test]
    public void Netty_array_pool_list_ref_encoding_uses_empty_list_for_null_withdrawals()
    {
        WithdrawalDecoder decoder = new();
        using ArrayPoolListRef<Withdrawal?> withdrawals = new(3);
        withdrawals.Add(TestItem.WithdrawalA_1Eth);
        withdrawals.Add(null);
        withdrawals.Add(TestItem.WithdrawalB_2Eth);

        using NettyRlpStream stream = decoder.EncodeToNewNettyStream(in withdrawals);

        AssertEncodedNullItem(decoder, stream.AsSpan());
    }

    [Test]
    public void Netty_collection_encoding_does_not_call_item_encoder_for_null_items()
    {
        NonNullableItemDecoder decoder = new();

        NonNullableItem?[] array = [new(), null, new()];
        using NettyRlpStream arrayStream = decoder.EncodeToNewNettyStream(array);
        AssertEncodedNullItem(arrayStream.AsSpan());

        List<NonNullableItem?> list = [new(), null, new()];
        using NettyRlpStream listStream = decoder.EncodeToNewNettyStream(list);
        AssertEncodedNullItem(listStream.AsSpan());

        using ArrayPoolListRef<NonNullableItem?> pooled = new(3);
        pooled.Add(new());
        pooled.Add(null);
        pooled.Add(new());

        using NettyRlpStream pooledStream = decoder.EncodeToNewNettyStream(in pooled);
        AssertEncodedNullItem(pooledStream.AsSpan());
    }

    private static void AssertEncodedNullItem(WithdrawalDecoder decoder, ReadOnlySpan<byte> bytes)
    {
        Rlp.ValueDecoderContext context = new(bytes);
        int sequenceEnd = context.ReadSequenceLength() + context.Position;

        Assert.That(decoder.Decode(ref context), Is.Not.Null);
        Assert.That(context.ReadByte(), Is.EqualTo(Rlp.EmptyListByte));
        Assert.That(decoder.Decode(ref context), Is.Not.Null);
        context.Check(sequenceEnd);
    }

    private static void AssertEncodedNullItem(ReadOnlySpan<byte> bytes)
    {
        Rlp.ValueDecoderContext context = new(bytes);
        int sequenceEnd = context.ReadSequenceLength() + context.Position;

        Assert.That(context.ReadByte(), Is.EqualTo(Rlp.EmptyByteArrayByte));
        Assert.That(context.ReadByte(), Is.EqualTo(Rlp.EmptyListByte));
        Assert.That(context.ReadByte(), Is.EqualTo(Rlp.EmptyByteArrayByte));
        context.Check(sequenceEnd);
    }

    private sealed class NonNullableItem
    {
    }

    private sealed class NonNullableItemDecoder : RlpDecoder<NonNullableItem>
    {
        public override int GetLength(NonNullableItem item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            ArgumentNullException.ThrowIfNull(item);
            return Rlp.OfEmptyByteArray.Length;
        }

        public override void Encode(RlpStream stream, NonNullableItem item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            ArgumentNullException.ThrowIfNull(item);
            stream.Encode(Rlp.OfEmptyByteArray);
        }

        protected override NonNullableItem DecodeInternal(ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None) =>
            throw new NotSupportedException();
    }
}
