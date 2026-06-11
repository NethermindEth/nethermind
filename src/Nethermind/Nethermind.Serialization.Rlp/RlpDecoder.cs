// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core.Buffers;

namespace Nethermind.Serialization.Rlp;

public abstract class RlpDecoder<T> : IRlpDecoder<T>
{
    public abstract int GetLength(T? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None);

    public abstract void Encode<TWriter>(ref TWriter stream, T item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        where TWriter : struct, IRlpWriteBackend, allows ref struct;

    [return: MaybeNull]
    protected abstract T DecodeInternal(ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None);

    public virtual Rlp Encode(T? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        byte[] bytes = new byte[GetLength(item, rlpBehaviors)];
        RlpWriter writer = new(bytes);
        Encode(ref writer, item!, rlpBehaviors);
        return new Rlp(bytes);
    }

    public virtual Rlp Encode(T?[]? items, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (items is null or [])
        {
            return Rlp.OfEmptyList;
        }

        byte[] bytes = new byte[GetLength(items, rlpBehaviors)];
        RlpWriter writer = new(bytes);
        Encode(ref writer, items, rlpBehaviors);
        return new Rlp(bytes);
    }

    [DoesNotReturn]
    [StackTraceHidden]
    protected static void ThrowRlpException(Exception exception) =>
        throw new RlpException($"Cannot decode stream of {typeof(T).Name}", exception);

    [return: MaybeNull]
    public T Decode(ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
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


    public virtual T[] DecodeArray(ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None, RlpLimit? limit = null)
    {
        int checkPosition = decoderContext.ReadSequenceLength() + decoderContext.Position;
        int length = decoderContext.PeekNumberOfItemsRemaining(checkPosition);
        decoderContext.GuardLimit(length, limit);
        T[] result = new T[length];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = DecodeGuardNotNull(ref decoderContext, rlpBehaviors);
        }

        if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) != RlpBehaviors.AllowExtraBytes)
        {
            decoderContext.Check(checkPosition);
        }

        return result;
    }

    [return: MaybeNull]
    public virtual T Decode(scoped ReadOnlySpan<byte> bytes, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        RlpReader context = new(bytes);
        return Decode(ref context, rlpBehaviors);
    }

    /// <inheritdoc/>
    [return: MaybeNull]
    public virtual T DecodeComplete(ref RlpReader context, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        T? value = Decode(ref context, rlpBehaviors);
        context.CheckEnd();
        return value;
    }

    /// <inheritdoc/>
    [return: MaybeNull]
    public virtual T DecodeComplete(scoped ReadOnlySpan<byte> bytes, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        RlpReader context = new(bytes);
        return DecodeComplete(ref context, rlpBehaviors);
    }

    public virtual T DecodeGuardNotNull(ref RlpReader context, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        T? value = Decode(ref context, rlpBehaviors);
        if (!typeof(T).IsValueType && value is null)
        {
            ThrowNullDecodedValue();
        }

        return value!;
    }

    /// <inheritdoc/>
    public virtual T DecodeCompleteNotNull(ref RlpReader context, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        T value = DecodeGuardNotNull(ref context, rlpBehaviors);
        context.CheckEnd();
        return value;
    }

    /// <inheritdoc/>
    public virtual T DecodeCompleteNotNull(scoped ReadOnlySpan<byte> bytes, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        RlpReader context = new(bytes);
        return DecodeCompleteNotNull(ref context, rlpBehaviors);
    }

    public virtual CappedArray<byte> EncodeToCappedArray(T? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None, ICappedArrayPool? bufferPool = null)
    {
        int size = GetNullableLength(item, rlpBehaviors);
        CappedArray<byte> buffer = bufferPool.SafeRent(size);
        RlpWriter writer = new(in buffer);
        if (item is null)
        {
            writer.EncodeNullObject();
        }
        else
        {
            Encode(ref writer, item, rlpBehaviors);
        }
        return buffer;
    }

    public virtual void Encode<TWriter>(ref TWriter writer, T?[]? items, RlpBehaviors behaviors = RlpBehaviors.None)
        where TWriter : struct, IRlpWriteBackend, allows ref struct
    {
        if (items is null)
        {
            writer.WriteByte(Rlp.EmptyListByte);
            return;
        }

        writer.StartSequence(GetContentLength(items, behaviors));
        for (int index = 0; index < items.Length; index++)
        {
            T? item = items[index];
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
