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
    public static ArrayPoolSpan<byte> EncodeToArrayPoolSpan<T>(this IRlpDecoder<T> decoder, T? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None, int stackSize = LengthStack.DefaultStackallocLength)
    {
        if (item is null)
        {
            return EmptyListSpan();
        }

        LengthStack lengths = new(stackalloc int[Math.Min(stackSize, LengthStack.MaxStackallocLength)]);
        try
        {
            ArrayPoolSpan<byte> buffer = new(decoder.PopulateLength(item, ref lengths, rlpBehaviors));
            try
            {
                RlpEncoderExtensions.EncodeTo(decoder, item, ref lengths, buffer, rlpBehaviors);
                return buffer;
            }
            catch
            {
                buffer.Dispose();
                throw;
            }
        }
        finally
        {
            lengths.Dispose();
        }
    }

    /// <summary>
    /// Encodes <paramref name="items"/> into a new disposable pooled byte span.
    /// </summary>
    public static ArrayPoolSpan<byte> EncodeToArrayPoolSpan<T>(this IRlpDecoder<T> decoder, scoped ReadOnlySpan<T?> items, RlpBehaviors behaviors = RlpBehaviors.None, int stackSizePerItem = 2)
    {
        LengthStack lengths = new(stackalloc int[Math.Min(items.Length * stackSizePerItem, LengthStack.MaxStackallocLength)]);
        try
        {
            ArrayPoolSpan<byte> buffer = new(decoder.PopulateSequenceLength(items, ref lengths, behaviors));
            try
            {
                RlpEncoderExtensions.EncodeSequenceTo(decoder, items, ref lengths, buffer, behaviors);
                return buffer;
            }
            catch
            {
                buffer.Dispose();
                throw;
            }
        }
        finally
        {
            lengths.Dispose();
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
    public static ArrayPoolList<byte> EncodeToArrayPoolList<T>(this IRlpDecoder<T> decoder, T item, RlpBehaviors rlpBehaviors = RlpBehaviors.None, int stackSize = LengthStack.DefaultStackallocLength)
    {
        LengthStack lengths = new(stackalloc int[Math.Min(stackSize, LengthStack.MaxStackallocLength)]);
        try
        {
            int length = decoder.PopulateLength(item, ref lengths, rlpBehaviors);
            ArrayPoolList<byte> buffer = new(length, length);
            try
            {
                RlpEncoderExtensions.EncodeTo(decoder, item, ref lengths, buffer.AsSpan(), rlpBehaviors);
                return buffer;
            }
            catch
            {
                buffer.Dispose();
                throw;
            }
        }
        finally
        {
            lengths.Dispose();
        }
    }

}
