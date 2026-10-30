// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core.Buffers;

namespace Nethermind.Serialization.Rlp;

public abstract class RlpDecoder<T> : IRlpDecoder<T>
{
    public abstract void PopulateLength(T item, in LengthListWriter lengths, RlpBehaviors rlpBehaviors = RlpBehaviors.None);

    public abstract void Encode<TWriter>(ref TWriter writer, T item, ref LengthList lengths, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        where TWriter : struct, IRlpWriteBackend, allows ref struct;

    [return: MaybeNull]
    protected abstract T DecodeInternal(ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None);

    public virtual Rlp Encode(T? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        => item is null ? Rlp.OfEmptyList : new Rlp(EncodeAsBytes(item, rlpBehaviors));

    /// <summary>
    /// Encodes <paramref name="item"/> into a freshly allocated <see cref="byte"/> array.
    /// </summary>
    /// <remarks>
    /// Equivalent to <see cref="Encode(T, RlpBehaviors)"/> without wrapping the result in a
    /// throwaway <see cref="Rlp"/>; prefer this when only the raw bytes are needed. The buffer is
    /// allocated uninitialized because <see cref="Encode{TWriter}(ref TWriter, T, ref LengthList, RlpBehaviors)"/>
    /// fills exactly the total length <see cref="PopulateLength"/> populates.
    /// </remarks>
    public byte[] EncodeAsBytes(T item, RlpBehaviors rlpBehaviors = RlpBehaviors.None, int stackSize = LengthList.DefaultStackallocLength)
    {
        LengthList lengths = new(stackalloc int[Math.Min(stackSize, LengthList.MaxStackallocLength)]);
        try
        {
            byte[] bytes = GC.AllocateUninitializedArray<byte>(this.PopulateLength(item, ref lengths, rlpBehaviors));
            RlpEncoderExtensions.EncodeTo(this, item, ref lengths, bytes, rlpBehaviors);
            return bytes;
        }
        finally
        {
            lengths.Dispose();
        }
    }

    public virtual Rlp Encode(T?[]? items, RlpBehaviors rlpBehaviors = RlpBehaviors.None, int stackSizePerItem = 2)
    {
        if (items is null or [])
        {
            return Rlp.OfEmptyList;
        }

        LengthList lengths = new(stackalloc int[Math.Min(items.Length * stackSizePerItem, LengthList.MaxStackallocLength)]);
        try
        {
            byte[] bytes = new byte[this.PopulateSequenceLength(items, ref lengths, rlpBehaviors)];
            RlpEncoderExtensions.EncodeSequenceTo(this, items, ref lengths, bytes, rlpBehaviors);
            return new Rlp(bytes);
        }
        finally
        {
            lengths.Dispose();
        }
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


    public virtual T?[] DecodeArray(ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None, RlpLimit? limit = null)
    {
        int checkPosition = decoderContext.ReadSequenceLength() + decoderContext.Position;
        int length = decoderContext.PeekNumberOfItemsRemaining(checkPosition, (limit ?? RlpLimit.DefaultLimit).Limit + 1);
        decoderContext.GuardLimit(length, limit);
        T?[] result = new T?[length];
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

    /// <inheritdoc/>
    public virtual T[] DecodeNonNullArray(ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None, RlpLimit? limit = null)
    {
        int checkPosition = decoderContext.ReadSequenceLength() + decoderContext.Position;
        int length = decoderContext.PeekNumberOfItemsRemaining(checkPosition, (limit ?? RlpLimit.DefaultLimit).Limit + 1);
        decoderContext.GuardLimit(length, limit);
        T[] result = new T[length];
        for (int i = 0; i < result.Length; i++)
        {
            if (decoderContext.PeekByte() == Rlp.EmptyListByte)
            {
                RlpHelpers.ThrowNullArrayElement(i);
            }

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

    [return: NotNull]
    public virtual T DecodeGuardNotNull(ref RlpReader context, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        T? value = Decode(ref context, rlpBehaviors);
        if (value is null)
        {
            ThrowNullDecodedValue();
        }

        return value!;
    }

    /// <inheritdoc/>
    [return: NotNull]
    public virtual T DecodeCompleteNotNull(ref RlpReader context, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        T value = DecodeGuardNotNull(ref context, rlpBehaviors);
        context.CheckEnd();
        return value;
    }

    /// <inheritdoc/>
    [return: NotNull]
    public virtual T DecodeCompleteNotNull(scoped ReadOnlySpan<byte> bytes, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        RlpReader context = new(bytes);
        return DecodeCompleteNotNull(ref context, rlpBehaviors);
    }

    public virtual CappedArray<byte> EncodeToCappedArray(T? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None, ICappedArrayPool? bufferPool = null, int stackSize = LengthList.DefaultStackallocLength)
    {
        if (item is null)
        {
            CappedArray<byte> nullBuffer = bufferPool.SafeRent(Rlp.OfEmptyList.Length);
            RlpWriter nullWriter = new(in nullBuffer);
            nullWriter.EncodeNullObject();
            return nullBuffer;
        }

        LengthList lengths = new(stackalloc int[Math.Min(stackSize, LengthList.MaxStackallocLength)]);
        try
        {
            CappedArray<byte> buffer = bufferPool.SafeRent(this.PopulateLength(item, ref lengths, rlpBehaviors));
            RlpEncoderExtensions.EncodeTo(this, item, ref lengths, buffer.AsSpan(), rlpBehaviors);
            return buffer;
        }
        finally
        {
            lengths.Dispose();
        }
    }

    [DoesNotReturn]
    [StackTraceHidden]
    private static T ThrowNullDecodedValue()
        => throw new RlpException($"{typeof(T).Name} decoding returned null");
}
