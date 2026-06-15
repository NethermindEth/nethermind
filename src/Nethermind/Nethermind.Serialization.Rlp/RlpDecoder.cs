// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core.Buffers;

namespace Nethermind.Serialization.Rlp;

public abstract class RlpDecoder<T> : IRlpDecoder<T>
{
    public abstract int GetLength(T item, RlpBehaviors rlpBehaviors = RlpBehaviors.None);

    public abstract void Encode(RlpStream stream, T item, RlpBehaviors rlpBehaviors = RlpBehaviors.None);

    public virtual void Encode(ref ValueRlpWriter writer, T item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        byte[] bytes = new byte[GetLength(item, rlpBehaviors)];
        Encode(new RlpStream(bytes), item, rlpBehaviors);
        writer.Write(bytes);
    }

    protected abstract T DecodeInternal(ref ValueRlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None);

    public virtual Rlp Encode(T item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        byte[] bytes = new byte[GetLength(item, rlpBehaviors)];
        ValueRlpWriter writer = new(bytes);
        Encode(ref writer, item, rlpBehaviors);
        return new Rlp(bytes);
    }

    public virtual Rlp Encode(T[] items, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (items is null or [])
        {
            return Rlp.OfEmptyList;
        }

        byte[] bytes = new byte[GetLength(items, rlpBehaviors)];
        ValueRlpWriter writer = new(bytes);
        Encode(ref writer, items, rlpBehaviors);
        return new Rlp(bytes);
    }

    [DoesNotReturn]
    [StackTraceHidden]
    protected static void ThrowRlpException(Exception exception) =>
        throw new RlpException($"Cannot decode stream of {typeof(T).Name}", exception);

    public T Decode(ref ValueRlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        try
        {
            return DecodeInternal(ref decoderContext, rlpBehaviors);
        }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            ThrowRlpException(e);
            return default;
        }
    }


    public virtual T[] DecodeArray(ref ValueRlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None, RlpLimit? limit = null)
    {
        int checkPosition = decoderContext.ReadSequenceLength() + decoderContext.Position;
        int length = decoderContext.PeekNumberOfItemsRemaining(checkPosition);
        decoderContext.GuardLimit(length, limit);
        T[] result = new T[length];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = Decode(ref decoderContext, rlpBehaviors);
        }

        if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) != RlpBehaviors.AllowExtraBytes)
        {
            decoderContext.Check(checkPosition);
        }

        return result;
    }

    public virtual T Decode(ReadOnlySpan<byte> bytes, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        ValueRlpReader context = new(bytes);
        return Decode(ref context, rlpBehaviors);
    }

    /// <inheritdoc/>
    public virtual T DecodeComplete(ref ValueRlpReader context, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        T value = Decode(ref context, rlpBehaviors);
        context.CheckEnd();
        return value;
    }

    /// <inheritdoc/>
    public virtual T DecodeComplete(ReadOnlySpan<byte> bytes, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        ValueRlpReader context = new(bytes);
        return DecodeComplete(ref context, rlpBehaviors);
    }

    public virtual T DecodeGuardNotNull(ref ValueRlpReader context, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        T value = Decode(ref context, rlpBehaviors);
        if (!typeof(T).IsValueType && value is null)
        {
            ThrowNullDecodedValue();
        }

        return value;
    }

    /// <inheritdoc/>
    public virtual T DecodeCompleteNotNull(ref ValueRlpReader context, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        T value = DecodeGuardNotNull(ref context, rlpBehaviors);
        context.CheckEnd();
        return value;
    }

    /// <inheritdoc/>
    public virtual T DecodeCompleteNotNull(ReadOnlySpan<byte> bytes, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        ValueRlpReader context = new(bytes);
        return DecodeCompleteNotNull(ref context, rlpBehaviors);
    }

    public virtual CappedArray<byte> EncodeToCappedArray(T? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None, ICappedArrayPool? bufferPool = null)
    {
        int size = GetLength(item, rlpBehaviors);
        CappedArray<byte> buffer = bufferPool.SafeRent(size);
        ValueRlpWriter writer = new(buffer);
        Encode(ref writer, item, rlpBehaviors);
        return buffer;
    }

    public virtual void Encode(RlpStream stream, T?[]? items, RlpBehaviors behaviors = RlpBehaviors.None)
    {
        if (items is null)
        {
            stream.Encode(Rlp.OfEmptyList);
            return;
        }

        stream.StartSequence(GetContentLength(items, behaviors));
        for (int index = 0; index < items.Length; index++)
        {
            T item = items[index];
            if (item is null)
            {
                stream.Encode(Rlp.OfEmptyList);
            }
            else
            {
                Encode(stream, item, behaviors);
            }
        }
    }

    public virtual void Encode(ref ValueRlpWriter writer, T?[]? items, RlpBehaviors behaviors = RlpBehaviors.None)
    {
        if (items is null)
        {
            writer.WriteByte(Rlp.EmptyListByte);
            return;
        }

        writer.StartSequence(GetContentLength(items, behaviors));
        for (int index = 0; index < items.Length; index++)
        {
            T item = items[index];
            if (item is null)
            {
                writer.WriteByte(Rlp.EmptyListByte);
            }
            else
            {
                Encode(ref writer, item, behaviors);
            }
        }
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
        => throw new RlpException($"{typeof(T).Name} decoding returned null");

    private int GetNullableLength(T? item, RlpBehaviors behaviors)
        => item is null ? Rlp.OfEmptyList.Length : GetLength(item, behaviors);
}
