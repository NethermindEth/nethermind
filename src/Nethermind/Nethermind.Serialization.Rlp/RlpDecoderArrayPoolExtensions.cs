// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.FluentRlp;

namespace Nethermind.Serialization.Rlp;

/// <summary>
/// Provides pool-backed RLP encoding helpers for decoders.
/// </summary>
/// <remarks>
/// Null tests start with <c>default(T) is null</c>, which folds at every tier, so Debug code does not box
/// a non-nullable struct to compare it with null.
/// </remarks>
public static class RlpDecoderArrayPoolExtensions
{
    // Encodings up to this length are hashed from a stack buffer.
    private const int MaxStackHashLength = 512;

    /// <summary>
    /// Encodes <paramref name="item"/> into a new disposable pooled byte span.
    /// </summary>
    public static ArrayPoolSpan<byte> EncodeToArrayPoolSpan<T>(this IRlpDecoder<T> decoder, T? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (default(T) is null && item is null)
        {
            return EmptyListSpan();
        }

        ArrayPoolSpan<byte> buffer = new(decoder.GetLength(item!, rlpBehaviors));
        try
        {
            decoder.EncodeExact(buffer, item!, rlpBehaviors);
            return buffer;
        }
        catch
        {
            buffer.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Encodes <paramref name="items"/> into a new disposable pooled byte span.
    /// </summary>
    public static ArrayPoolSpan<byte> EncodeToArrayPoolSpan<T>(this IRlpDecoder<T> decoder, scoped ReadOnlySpan<T?> items, RlpBehaviors behaviors = RlpBehaviors.None)
    {
        int totalLength = 0;
        for (int i = 0; i < items.Length; i++)
        {
            T? item = items[i];
            totalLength += default(T) is null && item is null ? Rlp.OfEmptyList.Length : decoder.GetLength(item!, behaviors);
        }

        int bufferLength = Rlp.LengthOfSequence(totalLength);

        ArrayPoolSpan<byte> buffer = new(bufferLength);
        try
        {
            RlpWriter writer = RlpWriter.ContentWriter(buffer);
            RlpSequence sequence = writer.StartSequence(totalLength);

            for (int i = 0; i < items.Length; i++)
            {
                T? item = items[i];
                if (default(T) is null && item is null)
                {
                    writer.EncodeNullObject();
                }
                else
                {
                    decoder.Encode(ref writer, item!, behaviors);
                }
            }

            writer.EndSequence(sequence);
            Rlp.CheckLength(in writer, bufferLength);
            return buffer;
        }
        catch
        {
            buffer.Dispose();
            throw;
        }
    }

    private static ArrayPoolSpan<byte> EmptyListSpan()
    {
        ArrayPoolSpan<byte> buffer = new(Rlp.OfEmptyList.Length);
        buffer[0] = Rlp.EmptyListByte;
        return buffer;
    }

    /// <summary>
    /// Encodes <paramref name="item"/> into a pool-rented <see cref="ArrayPoolList{T}"/> of bytes, producing
    /// the same bytes as <see cref="Rlp.Encode{T}(T, RlpBehaviors)"/> without the intermediate allocation. Ownership transfers
    /// to the caller, which MUST dispose the result.
    /// </summary>
    public static ArrayPoolList<byte> EncodeToArrayPoolList<T>(this IRlpDecoder<T> decoder, T item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        int length = decoder.GetLength(item, rlpBehaviors);
        ArrayPoolList<byte> buffer = new(length, length);
        try
        {
            decoder.EncodeExact(buffer.AsSpan(), item, rlpBehaviors);
            return buffer;
        }
        catch
        {
            buffer.Dispose();
            throw;
        }
    }

    /// <summary>Encodes <paramref name="item"/> to <paramref name="destination"/>, which has exactly the length of the item.</summary>
    /// <remarks>For callers that know the length already, so that it is not computed again.</remarks>
    /// <exception cref="InvalidOperationException">The decoder wrote another number of bytes than <paramref name="destination"/> has.</exception>
    internal static void EncodeExact<T>(this IRlpDecoder<T> decoder, Span<byte> destination, T item, RlpBehaviors rlpBehaviors)
    {
        RlpWriter writer = RlpWriter.ContentWriter(destination);
        decoder.Encode(ref writer, item, rlpBehaviors);
        Rlp.CheckLength(in writer, destination.Length);
    }

    /// <summary>Computes the Keccak hash of the encoding of <paramref name="item"/>.</summary>
    /// <remarks>Encodes to an exact-size stack or pooled buffer, then hashes the buffer once.</remarks>
    [SkipLocalsInit]
    public static ValueHash256 ComputeValueHash<T>(this IRlpDecoder<T> decoder, T item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        int length = decoder.GetLength(item, rlpBehaviors);
        if (length <= MaxStackHashLength)
        {
            Span<byte> buffer = stackalloc byte[MaxStackHashLength];
            return EncodeAndHash(decoder, buffer[..length], item, rlpBehaviors);
        }

        using ArrayPoolSpan<byte> pooled = new(length);
        return EncodeAndHash(decoder, pooled, item, rlpBehaviors);
    }

    /// <inheritdoc cref="ComputeValueHash{T}"/>
    public static Hash256 ComputeHash<T>(this IRlpDecoder<T> decoder, T item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        => new(decoder.ComputeValueHash(item, rlpBehaviors));

    private static ValueHash256 EncodeAndHash<T>(IRlpDecoder<T> decoder, Span<byte> buffer, T item, RlpBehaviors rlpBehaviors)
    {
        RlpWriter writer = RlpWriter.ContentWriter(buffer);
        decoder.Encode(ref writer, item, rlpBehaviors);
        Rlp.CheckLength(in writer, buffer.Length);
        return ValueKeccak.Compute(buffer);
    }
}
