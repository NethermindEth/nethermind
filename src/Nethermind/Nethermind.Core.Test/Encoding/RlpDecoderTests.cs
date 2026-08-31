// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Serialization.Rlp;
using Nethermind.Serialization.Rlp.Eip7928;
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
        RlpWriter writer = new(bytes);
        decoder.Encode(ref writer, withdrawals);
        RlpReader context = new(bytes);
        int sequenceEnd = context.ReadSequenceLength() + context.Position;

        Assert.That(decoder.Decode(ref context), Is.Not.Null);
        Assert.That(context.ReadByte(), Is.EqualTo(Rlp.EmptyListByte));
        Assert.That(decoder.Decode(ref context), Is.Not.Null);
        context.Check(sequenceEnd);
    }

    [Test]
    public void Decoder_array_legacy_api_preserves_null_elements()
    {
        WithdrawalDecoder decoder = new();
        Withdrawal?[] withdrawals = [TestItem.WithdrawalA_1Eth, null, TestItem.WithdrawalB_2Eth];
        Rlp encoded = decoder.Encode(withdrawals);
        RlpReader context = new(encoded.Bytes);

        Withdrawal?[] decoded = decoder.DecodeArray(ref context);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded, Has.Length.EqualTo(withdrawals.Length));
            Assert.That(decoded[0], Is.Not.Null);
            Assert.That(decoded[1], Is.Null);
            Assert.That(decoded[2], Is.Not.Null);
        }
    }

    [Test]
    public void Decoder_non_null_array_rejects_null_elements()
    {
        WithdrawalDecoder decoder = new();
        Rlp encoded = decoder.Encode(new Withdrawal?[] { null });

        void Decode()
        {
            RlpReader context = new(encoded.Bytes);
            decoder.DecodeNonNullArray(ref context);
        }

        Assert.That(Decode, Throws.TypeOf<RlpException>());
    }

    [Test]
    public void Decoder_non_null_array_rejects_empty_list_for_value_type_element()
    {
        static void Decode()
        {
            RlpReader context = new(new[] { (byte)0xc1, Rlp.EmptyListByte });
            UInt256Decoder.Instance.DecodeNonNullArray(ref context);
        }

        Assert.That(Decode, Throws.TypeOf<RlpException>().With.Message.Contains("null array element"));
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
            RlpReader context = new(ReadOnlySpan<byte>.Empty);
            decoder.Decode(ref context);
        }
    }

    [Test]
    public void Array_pool_span_encoding_uses_empty_list_for_null_withdrawals()
    {
        WithdrawalDecoder decoder = new();
        ReadOnlySpan<Withdrawal?> withdrawals = [TestItem.WithdrawalA_1Eth, null, TestItem.WithdrawalB_2Eth];

        using ArrayPoolSpan<byte> stream = decoder.EncodeToArrayPoolSpan(withdrawals);

        AssertEncodedNullItem(decoder, stream);
    }

    [Test]
    public void Array_pool_span_encoding_does_not_call_item_encoder_for_null_items()
    {
        NonNullableItemDecoder decoder = new();
        ReadOnlySpan<NonNullableItem?> items = [new(), null, new()];

        using ArrayPoolSpan<byte> stream = decoder.EncodeToArrayPoolSpan(items);

        AssertEncodedNullItem(stream);
    }

    [Test]
    public void Single_item_encoding_uses_empty_list_for_null_without_calling_item_length()
    {
        NonNullableItemDecoder decoder = new();

        Rlp rlp = decoder.Encode((NonNullableItem?)null);

        Assert.That(rlp.Bytes, Is.EqualTo(Rlp.OfEmptyList.Bytes));
    }

    [Test]
    public void Decode_delegate_array_rejects_empty_list_for_value_type_element()
    {
        static void Decode()
        {
            RlpReader context = new(new[] { (byte)0xc1, Rlp.EmptyListByte });
            context.DecodeNonNullArray(static (ref RlpReader c) => (c.DecodeKeccak(), c.DecodeULong()));
        }

        Assert.That(Decode, Throws.TypeOf<RlpException>().With.Message.Contains("null array element"));
    }

    [Test]
    public void Decode_array_pool_list_rejects_empty_list_for_value_type_element()
    {
        static void Decode()
        {
            RlpReader context = new(new[] { (byte)0xc1, Rlp.EmptyListByte });
            context.DecodeNonNullArrayPoolList(static (ref RlpReader c) => c.DecodeInt());
        }

        Assert.That(Decode, Throws.TypeOf<RlpException>().With.Message.Contains("null array element"));
    }

    [Test]
    public void Decode_delegate_array_legacy_api_uses_value_type_default()
    {
        RlpReader context = new(new[] { (byte)0xc1, Rlp.EmptyListByte });

        int[] result = context.DecodeArray(
            static (ref RlpReader _) => throw new InvalidOperationException(),
            defaultElement: 42);

        Assert.That(result, Is.EqualTo(new[] { 42 }));
    }

    [Test]
    public void Decode_array_pool_list_legacy_api_uses_value_type_default()
    {
        RlpReader context = new(new[] { (byte)0xc1, Rlp.EmptyListByte });

        using ArrayPoolList<int> result = context.DecodeArrayPoolList(
            static (ref RlpReader _) => throw new InvalidOperationException(),
            defaultElement: 42);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0], Is.EqualTo(42));
    }

    [Test]
    public void Decode_nullable_delegate_array_allows_null_reference_element()
    {
        RlpReader context = new(new[] { (byte)0xc1, Rlp.EmptyByteArrayByte });

        Hash256?[] result = context.DecodeNullableArray(static (ref RlpReader c) => c.DecodeKeccakOrNull());

        Assert.That(result, Has.Length.EqualTo(1));
        Assert.That(result[0], Is.Null);
    }

    [Test]
    public void Decode_non_null_array_rejects_empty_list_element()
    {
        static void Decode()
        {
            RlpReader context = new(new[] { (byte)0xc1, Rlp.EmptyListByte });
            context.DecodeNonNullArray(KeccakDecoder.Instance);
        }

        Assert.That(Decode, Throws.TypeOf<RlpException>().With.Message.Contains("null array element"));
    }

    [Test]
    public void Decode_array_legacy_allow_nulls_parameter_is_preserved()
    {
        RlpReader context = new(new[] { (byte)0xc1, Rlp.EmptyListByte });

        Hash256?[] result = context.DecodeArray(KeccakDecoder.Instance, allowNulls: true);

        Assert.That(result, Is.EqualTo(new Hash256?[] { null }));
    }

    [Test]
    public void Decode_nullable_delegate_array_uses_default_for_empty_list_element()
    {
        RlpReader context = new(new[] { (byte)0xc1, Rlp.EmptyListByte });

        Hash256?[] result = context.DecodeNullableArray(
            static (ref RlpReader _) => throw new InvalidOperationException(),
            defaultElement: TestItem.KeccakA);

        Assert.That(result, Has.Length.EqualTo(1));
        Assert.That(result[0], Is.EqualTo(TestItem.KeccakA));
    }

    [Test]
    public void Decode_nullable_array_pool_list_allows_null_reference_element()
    {
        RlpReader context = new(new[] { (byte)0xc1, Rlp.EmptyByteArrayByte });

        using ArrayPoolList<Hash256?> result = context.DecodeNullableArrayPoolList(static (ref RlpReader c) => c.DecodeKeccakOrNull());

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0], Is.Null);
    }

    [Test]
    public void Decode_nullable_array_pool_list_uses_default_for_empty_list_element()
    {
        RlpReader context = new(new[] { (byte)0xc1, Rlp.EmptyListByte });

        using ArrayPoolList<Hash256?> result = context.DecodeNullableArrayPoolList(
            static (ref RlpReader _) => throw new InvalidOperationException(),
            defaultElement: TestItem.KeccakA);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0], Is.EqualTo(TestItem.KeccakA));
    }

    private static void AssertEncodedNullItem(WithdrawalDecoder decoder, ReadOnlySpan<byte> bytes)
    {
        RlpReader context = new(bytes);
        int sequenceEnd = context.ReadSequenceLength() + context.Position;

        Assert.That(decoder.Decode(ref context), Is.Not.Null);
        Assert.That(context.ReadByte(), Is.EqualTo(Rlp.EmptyListByte));
        Assert.That(decoder.Decode(ref context), Is.Not.Null);
        context.Check(sequenceEnd);
    }

    private static void AssertEncodedNullItem(ReadOnlySpan<byte> bytes)
    {
        RlpReader context = new(bytes);
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
        public override int GetLength(NonNullableItem? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            ArgumentNullException.ThrowIfNull(item);
            return Rlp.OfEmptyByteArray.Length;
        }

        public override void Encode<TWriter>(ref TWriter writer, NonNullableItem item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            ArgumentNullException.ThrowIfNull(item);
            writer.Encode(Rlp.OfEmptyByteArray);
        }

        protected override NonNullableItem DecodeInternal(ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None) =>
            throw new NotSupportedException();
    }
}
