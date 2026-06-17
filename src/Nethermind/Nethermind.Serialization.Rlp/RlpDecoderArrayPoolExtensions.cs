// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Collections;

namespace Nethermind.Serialization.Rlp;

/// <summary>
/// Provides array-pool-backed RLP encoding helpers for decoders.
/// </summary>
public static class RlpDecoderArrayPoolExtensions
{
    /// <summary>
    /// Encodes <paramref name="item"/> into a new disposable pooled byte span.
    /// </summary>
    public static ArrayPoolSpan<byte> EncodeToArrayPoolSpan<T>(this IRlpDecoder<T> decoder, T? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item is null)
        {
            return EmptyListSpan();
        }

        ArrayPoolSpan<byte> encoded = new(decoder.GetLength(item, rlpBehaviors));
        RlpWriter writer = new((Span<byte>)encoded);
        decoder.Encode(ref writer, item, rlpBehaviors);
        return encoded;
    }

    /// <summary>
    /// Encodes <paramref name="items"/> into a new disposable pooled byte span.
    /// </summary>
    public static ArrayPoolSpan<byte> EncodeToArrayPoolSpan<T>(this IRlpDecoder<T> decoder, T?[]? items, RlpBehaviors behaviors = RlpBehaviors.None)
    {
        if (items is null)
        {
            return EmptyListSpan();
        }

        int totalLength = 0;
        for (int i = 0; i < items.Length; i++)
        {
            totalLength += GetNullableLength(decoder, items[i], behaviors);
        }

        int bufferLength = Rlp.LengthOfSequence(totalLength);

        ArrayPoolSpan<byte> encoded = new(bufferLength);
        RlpWriter writer = new((Span<byte>)encoded);
        writer.StartSequence(totalLength);

        for (int i = 0; i < items.Length; i++)
        {
            EncodeNullable(decoder, ref writer, items[i], behaviors);
        }

        return encoded;
    }

    /// <summary>
    /// Encodes <paramref name="items"/> into a new disposable pooled byte span.
    /// </summary>
    public static ArrayPoolSpan<byte> EncodeToArrayPoolSpan<T>(this IRlpDecoder<T> decoder, IList<T?>? items, RlpBehaviors behaviors = RlpBehaviors.None)
    {
        if (items is null)
        {
            return EmptyListSpan();
        }

        int totalLength = 0;
        for (int i = 0; i < items.Count; i++)
        {
            totalLength += GetNullableLength(decoder, items[i], behaviors);
        }

        int bufferLength = Rlp.LengthOfSequence(totalLength);

        ArrayPoolSpan<byte> encoded = new(bufferLength);
        RlpWriter writer = new((Span<byte>)encoded);
        writer.StartSequence(totalLength);

        for (int i = 0; i < items.Count; i++)
        {
            EncodeNullable(decoder, ref writer, items[i], behaviors);
        }

        return encoded;
    }

    /// <summary>
    /// Encodes <paramref name="items"/> into a new disposable pooled byte span.
    /// </summary>
    public static ArrayPoolSpan<byte> EncodeToArrayPoolSpan<T>(this IRlpDecoder<T> decoder, in ArrayPoolListRef<T?> items, RlpBehaviors behaviors = RlpBehaviors.None)
    {
        int totalLength = 0;
        for (int i = 0; i < items.Count; i++)
        {
            totalLength += GetNullableLength(decoder, items[i], behaviors);
        }

        int bufferLength = Rlp.LengthOfSequence(totalLength);

        ArrayPoolSpan<byte> encoded = new(bufferLength);
        RlpWriter writer = new((Span<byte>)encoded);
        writer.StartSequence(totalLength);

        for (int i = 0; i < items.Count; i++)
        {
            EncodeNullable(decoder, ref writer, items[i], behaviors);
        }

        return encoded;
    }

    private static ArrayPoolSpan<byte> EmptyListSpan()
    {
        ArrayPoolSpan<byte> empty = new(1);
        RlpWriter writer = new((Span<byte>)empty);
        writer.WriteByte(Rlp.EmptyListByte);
        return empty;
    }

    private static void EncodeNullable<TWriter, T>(IRlpDecoder<T> decoder, ref TWriter writer, T? item, RlpBehaviors behaviors)
        where TWriter : struct, IRlpWriteBackend, allows ref struct
    {
        if (item is null)
        {
            writer.WriteByte(Rlp.EmptyListByte);
            return;
        }

        decoder.Encode(ref writer, item, behaviors);
    }

    private static int GetNullableLength<T>(IRlpDecoder<T> decoder, T? item, RlpBehaviors behaviors)
        => item is null ? Rlp.OfEmptyList.Length : decoder.GetLength(item, behaviors);
}
