// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.Serialization.Rlp.Eip7928;
using NUnit.Framework;

namespace Nethermind.Core.Test.Encoding;

public class RlpDecoderTests
{
    [Test]
    public void Transaction_decoder_is_registered() =>
        Assert.That(Rlp.GetDecoder<Transaction>(), Is.SameAs(TxDecoder.Instance));

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

#pragma warning disable NUnit2045 // Each operation advances state required by the following check.
        Assert.That(decoder.Decode(ref context), Is.Not.Null);
#pragma warning restore NUnit2045
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
    public void Decoder_non_null_array_rejects_empty_list_element_before_decoding_it([Values] Subject subject, [Values(0, 1)] int index)
    {
        byte[] input = index == 0 ? [0xc2, Rlp.EmptyListByte, 0x01] : [0xc2, 0x01, Rlp.EmptyListByte];

        (RlpException? exception, int position, int guardCalls) = subject switch
        {
            Subject.Reference => Capture(new ReferenceItemDecoder(), input, DecodeApi.Array),
            Subject.NullableValue => Capture(new NullableIntDecoder(), input, DecodeApi.Array),
            _ => Capture(new IntDecoder(), input, DecodeApi.Array),
        };

        Assert.That(exception, Is.TypeOf<RlpException>());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(exception!.Message, Is.EqualTo($"Unexpected null array element at index {index}"));
            Assert.That(position, Is.EqualTo(index + 1), "The empty list must be rejected before it is consumed.");
            Assert.That(guardCalls, Is.EqualTo(index), "The empty list must not reach the element decoder.");
        }
    }

    [Test]
    public void Decoder_lists_reject_a_decoded_null(
        [Values(Subject.Reference, Subject.NullableValue)] Subject subject,
        [Values] DecodeApi api,
        [Values(0, 1)] int nullIndex)
    {
        // The test decoders consume DecodedNullMarker and return null, so the guard, not the 0xc0 check, must reject it.
        byte[] input = nullIndex == 0 ? [0xc2, DecodedNullMarker, 0x01] : [0xc2, 0x01, DecodedNullMarker];

        (RlpException? exception, int position, _) = subject == Subject.Reference
            ? Capture(new ReferenceItemDecoder(), input, api)
            : Capture(new NullableIntDecoder(), input, api);

        Assert.That(exception, Is.TypeOf<RlpException>());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(exception!.Message, Is.EqualTo(subject == Subject.Reference ? "ReferenceItem decoding returned null" : "Nullable`1 decoding returned null"));
            Assert.That(position, Is.EqualTo(nullIndex + 2), "The null must be rejected right after it is decoded.");
        }
    }

    [Test]
    public void Decoder_lists_keep_values_and_call_the_guard_once_per_element([Values] Subject subject, [Values(DecodeApi.Array, DecodeApi.Pool)] DecodeApi api)
    {
        switch (subject)
        {
            case Subject.Reference:
                AssertDecodedList(new ReferenceItemDecoder(), [0xc3, 0x01, 0x02, 0x03], api, [new(1), new(2), new(3)]);
                break;
            case Subject.NullableValue:
                AssertDecodedList<int?>(new NullableIntDecoder(), [0xc3, 0x01, Rlp.EmptyByteArrayByte, 0x02], api, [1, 0, 2]);
                break;
            default:
                // A non-nullable decoder returns default for the marker, and the default is a valid element.
                AssertDecodedList(new IntDecoder(), [0xc3, 0x02, DecodedNullMarker, 0x01], api, [2, 0, 1]);
                break;
        }
    }

    [Test]
    public void Null_items_encode_as_empty_list([Values(Subject.Reference, Subject.NullableValue)] Subject subject)
    {
        if (subject == Subject.Reference)
        {
            AssertNullEncoding(new ReferenceItemDecoder(), [new(1), null, new(2)], new ReferenceItem(2), [0xc3, 0x01, Rlp.EmptyListByte, 0x02]);
        }
        else
        {
            AssertNullEncoding<int?>(new NullableIntDecoder(), [1, null, 0], 0, [0xc3, 0x01, Rlp.EmptyListByte, Rlp.EmptyByteArrayByte]);
        }
    }

    [Test, NonParallelizable]
    public void Struct_null_checks_do_not_allocate_per_element([Values] AllocationCase operation)
    {
        const int count = 1024;
        long baseline = MeasureAllocations(operation, 1);
        long allocated = MeasureAllocations(operation, count);
        // DecodeNonNullArray returns a new array; the rest of the per-call cost is fixed and cancels with the baseline.
        long resultArrayGrowth = operation == AllocationCase.ArrayDecode ? (count - 1) * Unsafe.SizeOf<UInt256>() : 0;

        TestContext.Out.WriteLine($"{operation}: {allocated} bytes per {count}-element call, {baseline} per 1-element call");
        Assert.That(allocated - baseline - resultArrayGrowth, Is.LessThan((count - 1) * 8), "Null checks of a non-nullable struct must not allocate per element.");
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

        Assert.That(result, Is.EqualTo(new[] { 42 }));
    }

    [Test]
    public void Decode_nullable_delegate_array_allows_null_reference_element()
    {
        RlpReader context = new(new[] { (byte)0xc1, Rlp.EmptyByteArrayByte });

        Hash256?[] result = context.DecodeNullableArray(static (ref RlpReader c) => c.DecodeKeccakOrNull());

        Assert.That(result, Is.EqualTo(new Hash256?[] { null }));
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

        Assert.That(result, Is.EqualTo(new[] { TestItem.KeccakA }));
    }

    [Test]
    public void Decode_nullable_array_pool_list_allows_null_reference_element()
    {
        RlpReader context = new(new[] { (byte)0xc1, Rlp.EmptyByteArrayByte });

        using ArrayPoolList<Hash256?> result = context.DecodeNullableArrayPoolList(static (ref RlpReader c) => c.DecodeKeccakOrNull());

        Assert.That(result, Is.EqualTo(new Hash256?[] { null }));
    }

    [Test]
    public void Decode_nullable_array_pool_list_uses_default_for_empty_list_element()
    {
        RlpReader context = new(new[] { (byte)0xc1, Rlp.EmptyListByte });

        using ArrayPoolList<Hash256?> result = context.DecodeNullableArrayPoolList(
            static (ref RlpReader _) => throw new InvalidOperationException(),
            defaultElement: TestItem.KeccakA);

        Assert.That(result, Is.EqualTo(new[] { TestItem.KeccakA }));
    }

    public enum Subject { Reference, NullableValue, Value }

    public enum DecodeApi { Guard, Array, Pool }

    public enum AllocationCase { ArrayDecode, PooledDecode, ArrayEncode, CappedEncode }

    private const byte DecodedNullMarker = 0x7f;

    private static (RlpException? Exception, int Position, int GuardCalls) Capture<T>(RecordingDecoder<T> decoder, byte[] input, DecodeApi api)
    {
        RlpReader context = new(input);
        RlpException? exception = null;
        try
        {
            switch (api)
            {
                case DecodeApi.Guard:
                    context.ReadSequenceLength();
                    while (context.Position < input.Length) decoder.DecodeGuardNotNull(ref context);
                    break;
                case DecodeApi.Array:
                    decoder.DecodeNonNullArray(ref context);
                    break;
                default:
                    Rlp.DecodeArrayPool(ref context, decoder).Dispose();
                    break;
            }
        }
        catch (RlpException e)
        {
            exception = e;
        }

        return (exception, context.Position, decoder.GuardCalls);
    }

    private static void AssertDecodedList<T>(RecordingDecoder<T> decoder, byte[] input, DecodeApi api, T[] expected)
    {
        RlpReader context = new(input);
        T[] decoded;
        if (api == DecodeApi.Array)
        {
            decoded = decoder.DecodeNonNullArray(ref context);
        }
        else
        {
            using ArrayPoolList<T> list = Rlp.DecodeArrayPool(ref context, decoder);
            decoded = list.AsSpan().ToArray();
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded, Is.EqualTo(expected));
            Assert.That(context.Position, Is.EqualTo(input.Length));
            Assert.That(decoder.GuardCalls, Is.EqualTo(expected.Length), "Each element must go through DecodeGuardNotNull.");
        }
    }

    private static void AssertNullEncoding<T>(RlpDecoder<T> decoder, T?[] items, T present, byte[] expected)
    {
        byte[] presentBytes = expected[^1..];
        byte[] written = new byte[expected.Length + 1];
        RlpWriter writer = new(written);
        decoder.Encode(ref writer, items);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoder.Encode(default(T)).Bytes, Is.EqualTo(Rlp.OfEmptyList.Bytes), "scalar Encode");
            Assert.That(decoder.Encode(present).Bytes, Is.EqualTo(presentBytes), "scalar Encode");
            Assert.That(decoder.EncodeToCappedArray(default).ToArray(), Is.EqualTo(Rlp.OfEmptyList.Bytes), "EncodeToCappedArray");
            Assert.That(decoder.EncodeToCappedArray(present).ToArray(), Is.EqualTo(presentBytes), "EncodeToCappedArray");
            Assert.That(decoder.GetContentLength(items), Is.EqualTo(expected.Length - 1), "GetContentLength");
            Assert.That(decoder.GetLength(items), Is.EqualTo(expected.Length), "GetLength");
            Assert.That(decoder.Encode(items).Bytes, Is.EqualTo(expected), "array Encode");
            Assert.That(writer.Position, Is.EqualTo(expected.Length), "array Encode to a writer");
            Assert.That(written, Is.EqualTo((byte[])[.. expected, 0]), "array Encode to a writer");
        }
    }

    /// <summary>
    /// Mean bytes allocated by one <paramref name="operation"/> on <paramref name="count"/> distinct values after warm-up.
    /// Every call checks its whole result without allocating, so a dropped or stale result cannot pass.
    /// </summary>
    private static long MeasureAllocations(AllocationCase operation, int count)
    {
        UInt256Decoder decoder = UInt256Decoder.Instance;
        UInt256[] values = new UInt256[count];
        for (int i = 0; i < count; i++) values[i] = new UInt256((ulong)i * 0x9E3779B97F4A7C15UL + 1, (ulong)i);
        byte[] encoded = decoder.Encode(values).Bytes;
        int contentStart = encoded.Length - decoder.GetContentLength(values);
        byte[] output = new byte[encoded.Length];
        ReusedBufferPool pool = new(new byte[encoded.Length]);

        for (int i = 0; i < 100; i++) Run();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) Run();
        return (GC.GetAllocatedBytesForCurrentThread() - before) / 100;

        void Run()
        {
            RlpReader reader = new(encoded);
            switch (operation)
            {
                case AllocationCase.ArrayDecode:
                    Check(decoder.DecodeNonNullArray(ref reader), reader.Position == encoded.Length);
                    break;
                case AllocationCase.PooledDecode:
                    using (ArrayPoolList<UInt256> list = Rlp.DecodeArrayPool(ref reader, decoder)) Check(list.AsSpan(), reader.Position == encoded.Length);
                    break;
                case AllocationCase.ArrayEncode:
                    output.AsSpan().Clear();
                    RlpWriter writer = new(output);
                    decoder.Encode(ref writer, values);
                    if (writer.Position != encoded.Length || !output.AsSpan().SequenceEqual(encoded)) throw new InvalidOperationException("Encoding changed.");
                    break;
                case AllocationCase.CappedEncode:
                    int position = contentStart;
                    foreach (UInt256 value in values)
                    {
                        CappedArray<byte> item = decoder.EncodeToCappedArray(value, RlpBehaviors.None, pool);
                        if (!item.AsSpan().SequenceEqual(encoded.AsSpan(position, item.Length))) throw new InvalidOperationException("Encoding changed.");
                        position += item.Length;
                    }

                    if (position != encoded.Length) throw new InvalidOperationException("Encoding changed.");
                    break;
            }
        }

        void Check(ReadOnlySpan<UInt256> decoded, bool consumed)
        {
            if (!consumed || decoded.Length != count) throw new InvalidOperationException("Decoded count or length changed.");
            for (int i = 0; i < count; i++)
            {
                if (decoded[i] != values[i]) throw new InvalidOperationException("Decoded value changed.");
            }
        }
    }

    private sealed class ReusedBufferPool(byte[] array) : ICappedArrayPool
    {
        public CappedArray<byte> Rent(int size) => new(array, size);

        public void Return(in CappedArray<byte> buffer) { }
    }

    private sealed record ReferenceItem(int Value);

    /// <summary>
    /// Codec over RLP integers that returns a decoded null (default) for <see cref="DecodedNullMarker"/>, counts guard
    /// calls, and throws if a null item reaches its element encoder.
    /// </summary>
    private abstract class RecordingDecoder<T> : RlpDecoder<T>
    {
        public int GuardCalls { get; private set; }

        protected abstract T FromInt(int value);

        protected abstract int ToInt(T item);

        public override int GetLength(T item, RlpBehaviors rlpBehaviors = RlpBehaviors.None) => Rlp.LengthOf(ToInt(item));

        public override void Encode<TWriter>(ref TWriter writer, T item, RlpBehaviors rlpBehaviors = RlpBehaviors.None) => writer.Encode(ToInt(item));

        [return: NotNull]
        public override T DecodeGuardNotNull(ref RlpReader context, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            GuardCalls++;
            return base.DecodeGuardNotNull(ref context, rlpBehaviors);
        }

        [return: MaybeNull]
        protected override T DecodeInternal(ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (decoderContext.PeekByte() != DecodedNullMarker) return FromInt(decoderContext.DecodeInt());

            decoderContext.Position++;
            return default;
        }
    }

    private sealed class ReferenceItemDecoder : RecordingDecoder<ReferenceItem>
    {
        protected override ReferenceItem FromInt(int value) => new(value);

        protected override int ToInt(ReferenceItem item) => item?.Value ?? throw new InvalidOperationException("A null item reached the element encoder.");
    }

    private sealed class NullableIntDecoder : RecordingDecoder<int?>
    {
        protected override int? FromInt(int value) => value;

        protected override int ToInt(int? item) => item ?? throw new InvalidOperationException("A null item reached the element encoder.");
    }

    private sealed class IntDecoder : RecordingDecoder<int>
    {
        protected override int FromInt(int value) => value;

        protected override int ToInt(int item) => item;
    }

    private static void AssertEncodedNullItem(WithdrawalDecoder decoder, ReadOnlySpan<byte> bytes)
    {
        RlpReader context = new(bytes);
        int sequenceEnd = context.ReadSequenceLength() + context.Position;

#pragma warning disable NUnit2045 // Each operation advances state required by the following check.
        Assert.That(decoder.Decode(ref context), Is.Not.Null);
#pragma warning restore NUnit2045
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
