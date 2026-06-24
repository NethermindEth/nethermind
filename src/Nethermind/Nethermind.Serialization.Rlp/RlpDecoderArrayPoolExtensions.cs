// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;

namespace Nethermind.Serialization.Rlp;

/// <summary>
/// Provides pool-backed RLP encoding helpers for decoders.
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

        ArrayPoolSpan<byte> buffer = new(decoder.GetLength(item, rlpBehaviors));
        try
        {
            RlpWriter writer = new(buffer);
            decoder.Encode(ref writer, item, rlpBehaviors);
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
            totalLength += item is null ? Rlp.OfEmptyList.Length : decoder.GetLength(item, behaviors);
        }

        int bufferLength = Rlp.LengthOfSequence(totalLength);

        ArrayPoolSpan<byte> buffer = new(bufferLength);
        try
        {
            RlpWriter writer = new(buffer);
            writer.StartSequence(totalLength);

            for (int i = 0; i < items.Length; i++)
            {
                T? item = items[i];
                if (item is null)
                {
                    writer.EncodeNullObject();
                }
                else
                {
                    decoder.Encode(ref writer, item, behaviors);
                }
            }

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
        try
        {
            RlpWriter writer = new(buffer);
            writer.EncodeNullObject();
            return buffer;
        }
        catch
        {
            buffer.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Encodes <paramref name="item"/> into a pool-rented <see cref="ArrayPoolList{T}"/> of bytes, producing
    /// the same bytes as <see cref="Rlp.Encode{T}"/> without the intermediate allocation. Ownership transfers
    /// to the caller, which MUST dispose the result.
    /// </summary>
    public static ArrayPoolList<byte> EncodeToArrayPoolList<T>(this IRlpDecoder<T> decoder, T item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        int length = decoder.GetLength(item, rlpBehaviors);
        ArrayPoolList<byte> buffer = new(length, length);
        try
        {
            RlpWriter writer = new(new CappedArray<byte>(buffer.UnsafeGetInternalArray(), length));
            decoder.Encode(ref writer, item, rlpBehaviors);
            return buffer;
        }
        catch
        {
            buffer.Dispose();
            throw;
        }
    }

}
