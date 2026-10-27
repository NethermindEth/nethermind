// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;

namespace Nethermind.Serialization.Rlp;

public static class RlpEncoderExtensions
{
    extension<T>(IRlpDecoder<T> decoder)
    {
        /// <summary>Populates <paramref name="items"/> last to first, then pushes their content length and returns the sequence total.</summary>
        /// <remarks>A <see langword="null"/> item takes an empty list without reaching the decoder.</remarks>
        public int PopulateSequenceLength(ReadOnlySpan<T?> items, ref LengthStack lengths, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            int contentLength = 0;
            for (int i = items.Length - 1; i >= 0; i--)
            {
                contentLength += items[i] is null
                    ? Rlp.OfEmptyList.Length
                    : decoder.PopulateLength(items[i]!, ref lengths, rlpBehaviors);
            }

            lengths.Push(contentLength);
            return Rlp.LengthOfSequence(contentLength);
        }

        /// <summary>Writes a sequence populated by <see cref="PopulateSequenceLength"/>.</summary>
        public void EncodeSequence<TWriter>(ref TWriter writer, ReadOnlySpan<T?> items, ref LengthStack lengths, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
            where TWriter : struct, IRlpWriteBackend, allows ref struct
        {
            writer.StartSequence(lengths.Pop());
            foreach (T? item in items)
            {
                if (item is null)
                {
                    writer.WriteByte(Rlp.EmptyListByte);
                }
                else
                {
                    decoder.Encode(ref writer, item, ref lengths, rlpBehaviors);
                }
            }
        }

        /// <summary>Populates and encodes <paramref name="item"/> on its own stack, at the position of a caller's writer.</summary>
        public void PopulateAndEncode<TWriter>(ref TWriter writer, T item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
            where TWriter : struct, IRlpWriteBackend, allows ref struct
        {
            // Not stackalloc: the caller's writer may outlive this frame, so the stack must not reference it.
            LengthStack lengths = new(default);
            try
            {
                decoder.PopulateLength(item, ref lengths, rlpBehaviors);
                decoder.Encode(ref writer, item, ref lengths, rlpBehaviors);
            }
            finally
            {
                lengths.Dispose();
            }
        }

        public int GetEncodedLength(T item, RlpBehaviors rlpBehaviors = RlpBehaviors.None, int stackSize = LengthStack.DefaultStackallocLength)
        {
            LengthStack lengths = new(stackalloc int[stackSize]);
            try
            {
                return decoder.PopulateLength(item, ref lengths, rlpBehaviors);
            }
            finally
            {
                lengths.Dispose();
            }
        }

        public Hash256 CalculateHash(T item, RlpBehaviors rlpBehaviors = RlpBehaviors.None, int stackSize = LengthStack.DefaultStackallocLength) => new(decoder.CalculateValueHash(item, rlpBehaviors, stackSize));

        /// <summary>The Keccak hash of the encoding of <paramref name="item"/>, computed without materialising the bytes.</summary>
        public ValueHash256 CalculateValueHash(T item, RlpBehaviors rlpBehaviors = RlpBehaviors.None, int stackSize = LengthStack.DefaultStackallocLength)
        {
            LengthStack lengths = new(stackalloc int[stackSize]);
            scoped KeccakRlpWriter writer = new();
            try
            {
                decoder.PopulateLength(item, ref lengths, rlpBehaviors);
                decoder.Encode(ref writer, item, ref lengths, rlpBehaviors);
                return writer.GetValueHash();
            }
            finally
            {
                lengths.Dispose();
            }
        }
    }

    /// <summary>Encodes a populated item into <paramref name="destination"/>, which has room for the populated total.</summary>
    internal static void EncodeTo<T>(IRlpDecoder<T> decoder, T item, ref LengthStack lengths, Span<byte> destination, RlpBehaviors rlpBehaviors)
    {
        RlpWriter writer = new(destination);
        decoder.Encode(ref writer, item, ref lengths, rlpBehaviors);
    }

    /// <summary>Encodes a populated sequence into <paramref name="destination"/>, which has room for the populated total.</summary>
    internal static void EncodeSequenceTo<T>(IRlpDecoder<T> decoder, ReadOnlySpan<T?> items, ref LengthStack lengths, Span<byte> destination, RlpBehaviors rlpBehaviors)
    {
        RlpWriter writer = new(destination);
        decoder.EncodeSequence(ref writer, items, ref lengths, rlpBehaviors);
    }
}
