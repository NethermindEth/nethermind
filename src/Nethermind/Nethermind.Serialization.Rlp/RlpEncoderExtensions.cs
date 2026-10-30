// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;

namespace Nethermind.Serialization.Rlp;

public static class RlpEncoderExtensions
{
    extension<T>(IRlpDecoder<T> decoder)
    {
        /// <summary>Populates <paramref name="items"/> as one sequence.</summary>
        /// <remarks>A <see langword="null"/> item takes an empty list without reaching the decoder.</remarks>
        public void PopulateSequenceLength(ReadOnlySpan<T?> items, in LengthListWriter lengths, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            using LengthListWriter sequence = lengths.BeginContainer();
            foreach (T? item in items)
            {
                if (item is null)
                {
                    sequence.Add(Rlp.OfEmptyList.Length);
                }
                else
                {
                    decoder.PopulateLength(item, sequence, rlpBehaviors);
                }
            }
        }

        /// <summary>Populates <paramref name="item"/> as the only item of <paramref name="lengths"/>, clearing it first.</summary>
        /// <returns>The total encoded length of <paramref name="item"/>.</returns>
        public int PopulateLength(T item, ref LengthList lengths, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            LengthListWriter root = lengths.BeginRootContainer();
            decoder.PopulateLength(item, root, rlpBehaviors);
            return root.Length;
        }

        /// <summary>Populates <paramref name="items"/> as the only sequence of <paramref name="lengths"/>, clearing it first.</summary>
        /// <returns>The total encoded length of the sequence.</returns>
        public int PopulateSequenceLength(ReadOnlySpan<T?> items, ref LengthList lengths, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            LengthListWriter root = lengths.BeginRootContainer();
            decoder.PopulateSequenceLength(items, root, rlpBehaviors);
            return root.Length;
        }

        /// <summary>Writes a sequence populated by <see cref="PopulateSequenceLength(ReadOnlySpan{T}, LengthListWriter, RlpBehaviors)"/>.</summary>
        public void EncodeSequence<TWriter>(ref TWriter writer, ReadOnlySpan<T?> items, ref LengthList lengths, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
            where TWriter : struct, IRlpWriteBackend, allows ref struct
        {
            writer.StartSequence(lengths.Read());
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

        /// <summary>Populates and encodes <paramref name="item"/> at the position of a caller's writer.</summary>
        /// <param name="lengths">A list the caller owns and disposes; populating clears it first.</param>
        public void PopulateAndEncode<TWriter>(ref TWriter writer, T item, ref LengthList lengths, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
            where TWriter : struct, IRlpWriteBackend, allows ref struct
        {
            decoder.PopulateLength(item, ref lengths, rlpBehaviors);
            decoder.Encode(ref writer, item, ref lengths, rlpBehaviors);
        }

        public int GetEncodedLength(T item, RlpBehaviors rlpBehaviors = RlpBehaviors.None, int stackSize = LengthList.DefaultStackallocLength)
        {
            LengthList lengths = new(stackalloc int[Math.Min(stackSize, LengthList.MaxStackallocLength)]);
            try
            {
                return decoder.PopulateLength(item, ref lengths, rlpBehaviors);
            }
            finally
            {
                lengths.Dispose();
            }
        }

        public Hash256 CalculateHash(T item, RlpBehaviors rlpBehaviors = RlpBehaviors.None, int stackSize = LengthList.DefaultStackallocLength) => new(decoder.CalculateValueHash(item, rlpBehaviors, stackSize));

        /// <summary>The Keccak hash of the encoding of <paramref name="item"/>, computed without materialising the bytes.</summary>
        public ValueHash256 CalculateValueHash(T item, RlpBehaviors rlpBehaviors = RlpBehaviors.None, int stackSize = LengthList.DefaultStackallocLength)
        {
            LengthList lengths = new(stackalloc int[Math.Min(stackSize, LengthList.MaxStackallocLength)]);
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
    internal static void EncodeTo<T>(IRlpDecoder<T> decoder, T item, ref LengthList lengths, Span<byte> destination, RlpBehaviors rlpBehaviors)
    {
        RlpWriter writer = new(destination);
        decoder.Encode(ref writer, item, ref lengths, rlpBehaviors);
    }

    /// <summary>Encodes a populated sequence into <paramref name="destination"/>, which has room for the populated total.</summary>
    internal static void EncodeSequenceTo<T>(IRlpDecoder<T> decoder, ReadOnlySpan<T?> items, ref LengthList lengths, Span<byte> destination, RlpBehaviors rlpBehaviors)
    {
        RlpWriter writer = new(destination);
        decoder.EncodeSequence(ref writer, items, ref lengths, rlpBehaviors);
    }
}
