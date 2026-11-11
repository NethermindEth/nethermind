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
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;
using Nethermind.Serialization.Rlp.Eip7928;
using NUnit.Framework;

namespace Nethermind.Core.Test.Encoding;

public class RlpDecoderTests
{
    [Test]
    public void Transaction_decoder_is_registered() =>
        Assert.That(Rlp.GetDecoder<Transaction>(), Is.SameAs(TxDecoder.Instance));

    // A missing registration is a configuration error, so it must not look like malformed input.
    [Test]
    public void Missing_decoder_throws_not_supported()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.Throws<NotSupportedException>(static () => Rlp.GetDecoderOrThrow<NonNullableItem>());
            Assert.Throws<NotSupportedException>(static () =>
            {
                RlpReader reader = new(Rlp.OfEmptyByteArray.Bytes);
                Rlp.Decode<NonNullableItem>(ref reader);
            });
            Assert.Throws<NotSupportedException>(static () => Rlp.Encode(new NonNullableItem()));
        }
    }

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
        RlpWriter writer = RlpWriter.ContentWriter(bytes);
        decoder.Encode(ref writer, withdrawals);
        RlpReader context = new RlpReader(bytes).EnterSequence(out _);

#pragma warning disable NUnit2045 // Each operation advances state required by the following check.
        Assert.That(decoder.Decode(ref context), Is.Not.Null);
#pragma warning restore NUnit2045
        Assert.That(context.ReadRaw(1)[0], Is.EqualTo(Rlp.EmptyListByte));
        Assert.That(decoder.Decode(ref context), Is.Not.Null);
        context.CheckEnd();
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

        Assert.That(Decode, Throws.TypeOf<RlpReaderException>());
    }

    [Test]
    public void Decoder_non_null_array_rejects_empty_list_for_value_type_element()
    {
        static void Decode()
        {
            RlpReader context = new(new[] { (byte)0xc1, Rlp.EmptyListByte });
            UInt256Decoder.Instance.DecodeNonNullArray(ref context);
        }

        Assert.That(Decode, Throws.TypeOf<RlpReaderException>().With.Message.Contains("null array element"));
    }

    [Test]
    public void Decoder_non_null_array_rejects_empty_list_element_before_decoding_it([Values] Subject subject, [Values(0, 1)] int index)
    {
        byte[] input = index == 0 ? [0xc2, Rlp.EmptyListByte, 0x01] : [0xc2, 0x01, Rlp.EmptyListByte];

        (RlpReaderException? exception, int decodeCalls) = subject switch
        {
            Subject.Reference => Capture(new ReferenceItemDecoder(), input, DecodeApi.Array),
            Subject.NullableValue => Capture(new NullableIntDecoder(), input, DecodeApi.Array),
            _ => Capture(new IntDecoder(), input, DecodeApi.Array),
        };

        Assert.That(exception, Is.TypeOf<RlpReaderException>());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(exception!.Message, Is.EqualTo($"Unexpected null array element at index {index}"));
            Assert.That(decodeCalls, Is.EqualTo(index), "The empty list must not reach the element decoder.");
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

        (RlpReaderException? exception, int decodeCalls) = subject == Subject.Reference
            ? Capture(new ReferenceItemDecoder(), input, api)
            : Capture(new NullableIntDecoder(), input, api);

        Assert.That(exception, Is.TypeOf<RlpReaderException>());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(exception!.Message, Is.EqualTo(subject == Subject.Reference ? "ReferenceItem decoding returned null" : "Nullable`1 decoding returned null"));
            Assert.That(decodeCalls, Is.EqualTo(nullIndex + 1), "The null must be rejected right after it is decoded.");
        }
    }

    [Test]
    public void Decoder_lists_keep_values_and_decode_once_per_element([Values] Subject subject, [Values(DecodeApi.Array, DecodeApi.Pool)] DecodeApi api)
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

    // A regression guard in Debug builds only: optimized builds already fold these null checks, so the test passes there either way.
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
    public void Decode_of_empty_input_throws_truncated()
    {
        Assert.That(DecodeEmptyInput, Throws.TypeOf<RlpReaderException>().With.Message.Contains("truncated"));

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

        Assert.That(Decode, Throws.TypeOf<RlpReaderException>().With.Message.Contains("null array element"));
    }

    [Test]
    public void Decode_array_pool_list_rejects_empty_list_for_value_type_element()
    {
        static void Decode()
        {
            RlpReader context = new(new[] { (byte)0xc1, Rlp.EmptyListByte });
            context.DecodeNonNullArrayPoolList(static (ref RlpReader c) => c.DecodeInt());
        }

        Assert.That(Decode, Throws.TypeOf<RlpReaderException>().With.Message.Contains("null array element"));
    }

    [Test]
    public void Decode_delegate_lists_reject_a_null_decoded_reference_element([Values] bool pooled)
    {
        void Decode()
        {
            RlpReader context = new(new[] { (byte)0xc1, Rlp.EmptyByteArrayByte });
            if (pooled) context.DecodeNonNullArrayPoolList(static (ref RlpReader c) => c.DecodeKeccakOrNull()!).Dispose();
            else context.DecodeNonNullArray(static (ref RlpReader c) => c.DecodeKeccakOrNull()!);
        }

        Assert.That(Decode, Throws.TypeOf<RlpReaderException>().With.Message.Contains("null array element"));
    }

    [Test]
    public void Decode_delegate_lists_reject_a_null_decoded_nullable_value_element([Values] bool pooled)
    {
        void Decode()
        {
            RlpReader context = new(new[] { (byte)0xc1, Rlp.EmptyByteArrayByte });
            if (pooled) context.DecodeNonNullArrayPoolList<int?>(static (ref RlpReader c) => { c.SkipItem(); return null; }).Dispose();
            else context.DecodeNonNullArray<int?>(static (ref RlpReader c) => { c.SkipItem(); return null; });
        }

        Assert.That(Decode, Throws.TypeOf<RlpReaderException>().With.Message.Contains("null array element"));
    }

    // Deterministic in Debug builds; in Release it can only fail while the measured loop still runs at Tier0.
    [Test, NonParallelizable]
    public void Decode_delegate_array_of_structs_allocates_only_its_result()
    {
        byte[] encoded = [0xc2, 0x01, 0x02];
        UInt256[] result = [];
        for (int i = 0; i < 100; i++) result = Decode(encoded);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) result = Decode(encoded);
        long decoding = GC.GetAllocatedBytesForCurrentThread() - before;
        before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) result = NewResult();
        long resultArrays = GC.GetAllocatedBytesForCurrentThread() - before;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Has.Length.EqualTo(2));
            Assert.That(decoding - resultArrays, Is.LessThan(100 * 16), "Decoding a struct list must not box once per call.");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static UInt256[] NewResult() => new UInt256[2];

        static UInt256[] Decode(byte[] encoded)
        {
            RlpReader context = new(encoded);
            UInt256[] decoded = context.DecodeNonNullArray(static (ref RlpReader c) => c.DecodeUInt256());
            if (decoded.Length != 2 || decoded[1] != 2) throw new InvalidOperationException("Decoded list changed.");
            return decoded;
        }
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
    public void Decode_delegate_array_allows_null_reference_element()
    {
        RlpReader context = new(new[] { (byte)0xc1, Rlp.EmptyByteArrayByte });

        Hash256?[] result = context.DecodeArray(static (ref RlpReader c) => c.DecodeKeccakOrNull());

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

        Assert.That(Decode, Throws.TypeOf<RlpReaderException>().With.Message.Contains("null array element"));
    }

    [Test]
    public void Decode_array_legacy_allow_nulls_parameter_is_preserved()
    {
        RlpReader context = new(new[] { (byte)0xc1, Rlp.EmptyListByte });

        Hash256?[] result = context.DecodeArray(KeccakDecoder.Instance, allowNulls: true);

        Assert.That(result, Is.EqualTo(new Hash256?[] { null }));
    }

    [Test]
    public void Decode_delegate_array_uses_default_for_empty_list_element()
    {
        RlpReader context = new(new[] { (byte)0xc1, Rlp.EmptyListByte });

        Hash256?[] result = context.DecodeArray(
            static (ref RlpReader _) => throw new InvalidOperationException(),
            defaultElement: TestItem.KeccakA);

        Assert.That(result, Is.EqualTo(new[] { TestItem.KeccakA }));
    }

    // The exact-size buffers are not initialized, so an encoder that writes fewer bytes than it reports would leave
    // stale bytes. 2 bytes take the stack buffer of the hash and 513 bytes take a pooled one.
    [Test]
    public void Encoder_that_writes_less_than_its_length_throws([Values(2, 513)] int reportedLength)
    {
        ShortWritingDecoder decoder = new(reportedLength);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(() => decoder.EncodeAsBytes(new NonNullableItem()), Throws.InvalidOperationException);
            Assert.That(() => decoder.ComputeValueHash(new NonNullableItem()), Throws.InvalidOperationException);
        }
    }

    public enum Subject { Reference, NullableValue, Value }

    public enum DecodeApi { Guard, Array, Pool }

    public enum AllocationCase { ArrayDecode, PooledDecode, ArrayEncode, CappedEncode, SpanEncode, ItemSpanEncode }

    private const byte DecodedNullMarker = 0x7f;

    private static (RlpReaderException? Exception, int DecodeCalls) Capture<T>(RecordingDecoder<T> decoder, byte[] input, DecodeApi api)
    {
        RlpReader context = new(input);
        RlpReaderException? exception = null;
        try
        {
            switch (api)
            {
                case DecodeApi.Guard:
                    RlpReader items = context.EnterSequence(out _);
                    while (items.HasNext) decoder.DecodeGuardNotNull(ref items);
                    break;
                case DecodeApi.Array:
                    decoder.DecodeNonNullArray(ref context);
                    break;
                default:
                    Rlp.DecodeArrayPool(ref context, decoder).Dispose();
                    break;
            }
        }
        catch (RlpReaderException e)
        {
            exception = e;
        }

        return (exception, decoder.DecodeCalls);
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
            Assert.That(context.HasNext, Is.False);
            Assert.That(decoder.DecodeCalls, Is.EqualTo(expected.Length), "Each element must be decoded once.");
        }
    }

    private static void AssertNullEncoding<T>(RlpDecoder<T> decoder, T?[] items, T present, byte[] expected)
    {
        byte[] presentBytes = expected[^1..];
        byte[] written = new byte[expected.Length + 1];
        RlpWriter writer = RlpWriter.ContentWriter(written);
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
            Assert.That(writer.Length, Is.EqualTo(expected.Length), "array Encode to a writer");
            Assert.That(written, Is.EqualTo((byte[])[.. expected, 0]), "array Encode to a writer");
            using ArrayPoolSpan<byte> scalarSpan = decoder.EncodeToArrayPoolSpan(default(T));
            Assert.That(((ReadOnlySpan<byte>)scalarSpan).ToArray(), Is.EqualTo(Rlp.OfEmptyList.Bytes), "EncodeToArrayPoolSpan");
            using ArrayPoolSpan<byte> span = decoder.EncodeToArrayPoolSpan(new ReadOnlySpan<T?>(items));
            Assert.That(((ReadOnlySpan<byte>)span).ToArray(), Is.EqualTo(expected), "EncodeToArrayPoolSpan of items");
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
                    Check(decoder.DecodeNonNullArray(ref reader), !reader.HasNext);
                    break;
                case AllocationCase.PooledDecode:
                    using (ArrayPoolList<UInt256> list = Rlp.DecodeArrayPool(ref reader, decoder)) Check(list.AsSpan(), !reader.HasNext);
                    break;
                case AllocationCase.ArrayEncode:
                    output.AsSpan().Clear();
                    RlpWriter writer = RlpWriter.ContentWriter(output);
                    decoder.Encode(ref writer, values);
                    if (writer.Length != encoded.Length || !output.AsSpan().SequenceEqual(encoded)) throw new InvalidOperationException("Encoding changed.");
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
                case AllocationCase.SpanEncode:
                    using (ArrayPoolSpan<byte> span = decoder.EncodeToArrayPoolSpan((ReadOnlySpan<UInt256>)values))
                    {
                        if (!((ReadOnlySpan<byte>)span).SequenceEqual(encoded)) throw new InvalidOperationException("Encoding changed.");
                    }

                    break;
                case AllocationCase.ItemSpanEncode:
                    int offset = contentStart;
                    foreach (UInt256 value in values)
                    {
                        using ArrayPoolSpan<byte> span = decoder.EncodeToArrayPoolSpan(value);
                        ReadOnlySpan<byte> bytes = span;
                        if (!bytes.SequenceEqual(encoded.AsSpan(offset, bytes.Length))) throw new InvalidOperationException("Encoding changed.");
                        offset += bytes.Length;
                    }

                    if (offset != encoded.Length) throw new InvalidOperationException("Encoding changed.");
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
    /// Codec over RLP integers that returns a decoded null (default) for <see cref="DecodedNullMarker"/>, counts item
    /// decodes, and throws if a null item reaches its element encoder.
    /// </summary>
    private abstract class RecordingDecoder<T> : RlpDecoder<T>
    {
        public int DecodeCalls { get; private set; }

        protected abstract T FromInt(int value);

        protected abstract int ToInt(T item);

        public override int GetLength(T item, RlpBehaviors rlpBehaviors = RlpBehaviors.None) => Rlp.LengthOf(ToInt(item));

        public override void Encode(ref RlpWriter writer, T item, RlpBehaviors rlpBehaviors = RlpBehaviors.None) => writer.Encode(ToInt(item));

        [return: MaybeNull]
        protected override T DecodeInternal(ref RlpReader reader, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            DecodeCalls++;
            ReadOnlySpan<byte> encoded = reader.ReadEncodedItem();
            if (encoded is [DecodedNullMarker]) return default;

            RlpReader item = new(encoded);
            return FromInt(item.DecodeInt());
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
        RlpReader context = new RlpReader(bytes).EnterSequence(out _);

#pragma warning disable NUnit2045 // Each operation advances state required by the following check.
        Assert.That(decoder.Decode(ref context), Is.Not.Null);
#pragma warning restore NUnit2045
        Assert.That(context.ReadRaw(1)[0], Is.EqualTo(Rlp.EmptyListByte));
        Assert.That(decoder.Decode(ref context), Is.Not.Null);
        context.CheckEnd();
    }

    private static void AssertEncodedNullItem(ReadOnlySpan<byte> bytes)
    {
        RlpReader context = new RlpReader(bytes).EnterSequence(out _);

        Assert.That(context.ReadRaw(1)[0], Is.EqualTo(Rlp.EmptyByteArrayByte));
        Assert.That(context.ReadRaw(1)[0], Is.EqualTo(Rlp.EmptyListByte));
        Assert.That(context.ReadRaw(1)[0], Is.EqualTo(Rlp.EmptyByteArrayByte));
        context.CheckEnd();
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

        public override void Encode(ref RlpWriter writer, NonNullableItem item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            ArgumentNullException.ThrowIfNull(item);
            writer.Encode(Rlp.OfEmptyByteArray);
        }

        protected override NonNullableItem DecodeInternal(ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None) =>
            throw new NotSupportedException();
    }

    /// <summary>Reports <paramref name="length"/> bytes and writes one, as a scalar, so that no sequence check throws first.</summary>
    private sealed class ShortWritingDecoder(int length) : RlpDecoder<NonNullableItem>
    {
        public override int GetLength(NonNullableItem? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None) => length;

        public override void Encode(ref RlpWriter writer, NonNullableItem item, RlpBehaviors rlpBehaviors = RlpBehaviors.None) =>
            writer.Encode(Rlp.OfEmptyByteArray);

        protected override NonNullableItem DecodeInternal(ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None) =>
            throw new NotSupportedException();
    }
}
