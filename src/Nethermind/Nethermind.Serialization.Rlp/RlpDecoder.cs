// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core.Buffers;
using Nethermind.Serialization.FluentRlp;

namespace Nethermind.Serialization.Rlp;

public abstract class RlpDecoder<T> : IRlpDecoder<T>
{
    public abstract int GetLength(T item, RlpBehaviors rlpBehaviors = RlpBehaviors.None);

    public abstract void Encode(ref RlpWriter writer, T item, RlpBehaviors rlpBehaviors = RlpBehaviors.None);

    [return: MaybeNull]
    protected abstract T DecodeInternal(ref RlpReader reader, RlpBehaviors rlpBehaviors = RlpBehaviors.None);

    public virtual Rlp Encode(T? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        => item is null ? Rlp.OfEmptyList : new Rlp(EncodeAsBytes(item, rlpBehaviors));

    /// <summary>
    /// Encodes <paramref name="item"/> into a freshly allocated <see cref="byte"/> array.
    /// </summary>
    /// <remarks>
    /// Equivalent to <see cref="Encode(T, RlpBehaviors)"/> without wrapping the result in a
    /// throwaway <see cref="Rlp"/>; prefer this when only the raw bytes are needed. The buffer is
    /// allocated uninitialized because <see cref="Encode(ref RlpWriter, T, RlpBehaviors)"/>
    /// fills exactly <see cref="GetLength(T, RlpBehaviors)"/> bytes, which is checked.
    /// </remarks>
    public byte[] EncodeAsBytes(T item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        byte[] bytes = GC.AllocateUninitializedArray<byte>(GetLength(item, rlpBehaviors));
        this.EncodeTo(bytes, item, rlpBehaviors);
        return bytes;
    }

    public virtual Rlp Encode(T?[]? items, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (items is null or [])
        {
            return Rlp.OfEmptyList;
        }

        byte[] bytes = GC.AllocateUninitializedArray<byte>(GetLength(items, rlpBehaviors));
        RlpWriter writer = RlpWriter.ContentWriter(bytes);
        Encode(ref writer, items, rlpBehaviors);
        Rlp.CheckLength(in writer, bytes.Length);
        return new Rlp(bytes);
    }

    [DoesNotReturn]
    [StackTraceHidden]
    protected static void ThrowRlpException(Exception exception) =>
        throw new RlpReaderException($"Cannot decode stream of {typeof(T).Name}", exception);

    [return: MaybeNull]
    public T Decode(ref RlpReader reader, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        try
        {
            return DecodeInternal(ref reader, rlpBehaviors);
        }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            ThrowRlpException(e);
            return default;
        }
    }

    public virtual T?[] DecodeArray(ref RlpReader reader, RlpBehaviors rlpBehaviors = RlpBehaviors.None, RlpLimit? limit = null)
    {
        RlpReader items = reader.EnterSequence(out int itemLength);
        T?[] result = new T?[items.CountItems(limit)];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = Decode(ref items, rlpBehaviors);
        }

        reader.ExitSequence(in items, itemLength);
        return result;
    }

    /// <inheritdoc/>
    public virtual T[] DecodeNonNullArray(ref RlpReader reader, RlpBehaviors rlpBehaviors = RlpBehaviors.None, RlpLimit? limit = null)
    {
        RlpReader items = reader.EnterSequence(out int itemLength);
        T[] result = new T[items.CountItems(limit)];
        for (int i = 0; i < result.Length; i++)
        {
            if (items.IsNextItemEmptyList())
            {
                ThrowNullArrayElement(i);
            }

            result[i] = DecodeGuardNotNull(ref items, rlpBehaviors);
        }

        reader.ExitSequence(in items, itemLength);
        return result;
    }

    [return: MaybeNull]
    public virtual T Decode(scoped ReadOnlySpan<byte> bytes, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        RlpReader reader = new(bytes);
        return Decode(ref reader, rlpBehaviors);
    }

    /// <inheritdoc/>
    [return: MaybeNull]
    public virtual T DecodeComplete(ref RlpReader reader, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        T? value = Decode(ref reader, rlpBehaviors);
        reader.CheckEnd();
        return value;
    }

    /// <inheritdoc/>
    [return: MaybeNull]
    public virtual T DecodeComplete(scoped ReadOnlySpan<byte> bytes, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        RlpReader reader = new(bytes);
        return DecodeComplete(ref reader, rlpBehaviors);
    }

    [return: NotNull]
    public virtual T DecodeGuardNotNull(ref RlpReader reader, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        T? value = Decode(ref reader, rlpBehaviors);
        if (value is null)
        {
            ThrowNullDecodedValue();
        }

        return value!;
    }

    /// <inheritdoc/>
    [return: NotNull]
    public virtual T DecodeCompleteNotNull(ref RlpReader reader, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        T value = DecodeGuardNotNull(ref reader, rlpBehaviors);
        reader.CheckEnd();
        return value;
    }

    /// <inheritdoc/>
    [return: NotNull]
    public virtual T DecodeCompleteNotNull(scoped ReadOnlySpan<byte> bytes, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        RlpReader reader = new(bytes);
        return DecodeCompleteNotNull(ref reader, rlpBehaviors);
    }

    public virtual CappedArray<byte> EncodeToCappedArray(T? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None, ICappedArrayPool? bufferPool = null)
    {
        int size = GetNullableLength(item, rlpBehaviors);
        CappedArray<byte> buffer = bufferPool.SafeRent(size);
        RlpWriter writer = RlpWriter.ContentWriter(buffer.AsSpan());
        if (item is null)
        {
            writer.EncodeNullObject();
        }
        else
        {
            Encode(ref writer, item, rlpBehaviors);
        }

        Rlp.CheckLength(in writer, size);
        return buffer;
    }

    public virtual void Encode(ref RlpWriter writer, T?[]? items, RlpBehaviors behaviors = RlpBehaviors.None)
    {
        if (items is null)
        {
            writer.EncodeNullObject();
            return;
        }

        RlpSequence sequence = writer.StartSequence(GetContentLength(items, behaviors));
        for (int index = 0; index < items.Length; index++)
        {
            T? item = items[index];
            if (item is null)
            {
                writer.EncodeNullObject();
            }
            else
            {
                Encode(ref writer, item, behaviors);
            }
        }

        writer.EndSequence(sequence);
    }

    public virtual int GetContentLength(T?[]? items, RlpBehaviors behaviors = RlpBehaviors.None)
    {
        if (items is null)
        {
            return 0;
        }

        int contentLength = 0;
        for (int i = 0; i < items.Length; i++)
        {
            contentLength += GetNullableLength(items[i], behaviors);
        }

        return contentLength;
    }

    public virtual int GetLength(T?[]? items, RlpBehaviors behaviors = RlpBehaviors.None)
    {
        if (items is null)
        {
            return Rlp.OfEmptyList.Length;
        }

        return Rlp.LengthOfSequence(GetContentLength(items, behaviors));
    }

    [DoesNotReturn]
    [StackTraceHidden]
    private static T ThrowNullDecodedValue()
        => throw new RlpReaderException($"{typeof(T).Name} decoding returned null");

    [DoesNotReturn]
    [StackTraceHidden]
    private static void ThrowNullArrayElement(int index)
        => throw new RlpReaderException($"Unexpected null array element at index {index}");

    private int GetNullableLength(T? item, RlpBehaviors behaviors)
        => item is null ? Rlp.OfEmptyList.Length : GetLength(item, behaviors);
}
