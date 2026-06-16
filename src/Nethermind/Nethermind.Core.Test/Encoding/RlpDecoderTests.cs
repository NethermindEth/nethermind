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
            Assert.That(decoder.GetContentLength(null), Is.Zero);
            Assert.That(decoder.GetLength(null), Is.EqualTo(Rlp.OfEmptyList.Length));
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

        byte[] bytes = new byte[decoder.GetLength(withdrawals)];
        ValueRlpWriter writer = new(bytes);
        decoder.Encode(ref writer, withdrawals);
        ValueRlpReader context = new(bytes);
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
            ValueRlpReader context = new(ReadOnlySpan<byte>.Empty);
            decoder.Decode(ref context);
        }
    }

    [Test]
    public void Array_pool_span_array_encoding_uses_empty_list_for_null_withdrawals()
    {
        WithdrawalDecoder decoder = new();
        Withdrawal?[] withdrawals = [TestItem.WithdrawalA_1Eth, null, TestItem.WithdrawalB_2Eth];

        using ArrayPoolSpan<byte> stream = decoder.EncodeToArrayPoolSpan(withdrawals);

        AssertEncodedNullItem(decoder, stream);
    }

    [Test]
    public void Array_pool_span_list_encoding_uses_empty_list_for_null_withdrawals()
    {
        WithdrawalDecoder decoder = new();
        List<Withdrawal?> withdrawals = [TestItem.WithdrawalA_1Eth, null, TestItem.WithdrawalB_2Eth];

        using ArrayPoolSpan<byte> stream = decoder.EncodeToArrayPoolSpan(withdrawals);

        AssertEncodedNullItem(decoder, stream);
    }

    [Test]
    public void Array_pool_span_array_pool_list_ref_encoding_uses_empty_list_for_null_withdrawals()
    {
        WithdrawalDecoder decoder = new();
        using ArrayPoolListRef<Withdrawal?> withdrawals = new(3);
        withdrawals.Add(TestItem.WithdrawalA_1Eth);
        withdrawals.Add(null);
        withdrawals.Add(TestItem.WithdrawalB_2Eth);

        using ArrayPoolSpan<byte> stream = decoder.EncodeToArrayPoolSpan(in withdrawals);

        AssertEncodedNullItem(decoder, stream);
    }

    [Test]
    public void Array_pool_span_collection_encoding_does_not_call_item_encoder_for_null_items()
    {
        NonNullableItemDecoder decoder = new();

        NonNullableItem?[] array = [new(), null, new()];
        using ArrayPoolSpan<byte> arrayStream = decoder.EncodeToArrayPoolSpan(array);
        AssertEncodedNullItem(arrayStream);

        List<NonNullableItem?> list = [new(), null, new()];
        using ArrayPoolSpan<byte> listStream = decoder.EncodeToArrayPoolSpan(list);
        AssertEncodedNullItem(listStream);

        using ArrayPoolListRef<NonNullableItem?> pooled = new(3);
        pooled.Add(new());
        pooled.Add(null);
        pooled.Add(new());

        using ArrayPoolSpan<byte> pooledStream = decoder.EncodeToArrayPoolSpan(in pooled);
        AssertEncodedNullItem(pooledStream);
    }

    private static void AssertEncodedNullItem(WithdrawalDecoder decoder, ReadOnlySpan<byte> bytes)
    {
        ValueRlpReader context = new(bytes);
        int sequenceEnd = context.ReadSequenceLength() + context.Position;

        Assert.That(decoder.Decode(ref context), Is.Not.Null);
        Assert.That(context.ReadByte(), Is.EqualTo(Rlp.EmptyListByte));
        Assert.That(decoder.Decode(ref context), Is.Not.Null);
        context.Check(sequenceEnd);
    }

    private static void AssertEncodedNullItem(ReadOnlySpan<byte> bytes)
    {
        ValueRlpReader context = new(bytes);
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

        public override void Encode(ref ValueRlpWriter writer, NonNullableItem item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            ArgumentNullException.ThrowIfNull(item);
            writer.Encode(Rlp.OfEmptyByteArray);
        }

        protected override NonNullableItem DecodeInternal(ref ValueRlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None) =>
            throw new NotSupportedException();
    }
}
