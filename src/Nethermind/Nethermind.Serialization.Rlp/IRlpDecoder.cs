// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;

namespace Nethermind.Serialization.Rlp;

public interface IRlpDecoder;

public interface IRlpDecoder<T> : IRlpDecoder
{
    int GetLength(T item, RlpBehaviors rlpBehaviors = RlpBehaviors.None);

    void Encode(RlpStream stream, T item, RlpBehaviors rlpBehaviors = RlpBehaviors.None);

    Rlp Encode(T item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        byte[] bytes = new byte[GetLength(item, rlpBehaviors)];
        Encode(new RlpStream(bytes), item, rlpBehaviors);
        return new Rlp(bytes);
    }

    Rlp Encode(T[] items, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (items is null or [])
        {
            return Rlp.OfEmptyList;
        }

        byte[] bytes = new byte[GetLength(items, rlpBehaviors)];
        Encode(new RlpStream(bytes), items, rlpBehaviors);
        return new Rlp(bytes);
    }

    T Decode(ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None);

    T[] DecodeArray(ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None, RlpLimit? limit = null)
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

    T Decode(ReadOnlySpan<byte> bytes, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        Rlp.ValueDecoderContext context = new(bytes);
        return Decode(ref context, rlpBehaviors);
    }

    /// <summary>
    /// Decodes instance of <typeparamref name="T"/> from <paramref name="context"/>
    /// and verifies that the end of the stream has been reached.
    /// </summary>
    T DecodeComplete(ref Rlp.ValueDecoderContext context, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        T value = Decode(ref context, rlpBehaviors);
        context.CheckEnd();
        return value;
    }

    /// <summary>
    /// Decodes instance of <typeparamref name="T"/> from <paramref name="bytes"/>
    /// and verifies that the end of the stream has been reached.
    /// </summary>
    T DecodeComplete(ReadOnlySpan<byte> bytes, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        Rlp.ValueDecoderContext context = new(bytes);
        return DecodeComplete(ref context, rlpBehaviors);
    }

    T DecodeGuardNotNull(ref Rlp.ValueDecoderContext context, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        T value = Decode(ref context, rlpBehaviors);
        if (!typeof(T).IsValueType && value is null)
        {
            ThrowNullDecodedValue();
        }

        return value;
    }

    T DecodeGuardNotNull(ReadOnlySpan<byte> bytes, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        Rlp.ValueDecoderContext context = new(bytes);
        return DecodeGuardNotNull(ref context, rlpBehaviors);
    }

    /// <summary>
    /// Decodes instance of <typeparamref name="T"/> from <paramref name="context"/>
    /// and verifies that the end of the stream has been reached.
    /// Throws if decoded value is <c>null</c>.
    /// </summary>
    T DecodeCompleteNotNull(ref Rlp.ValueDecoderContext context, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        T value = DecodeGuardNotNull(ref context, rlpBehaviors);
        context.CheckEnd();
        return value;
    }

    /// <summary>
    /// Decodes instance of <typeparamref name="T"/> from <paramref name="bytes"/>
    /// and verifies that the end of the stream has been reached.
    /// Throws if decoded value is <c>null</c>.
    /// </summary>
    T DecodeCompleteNotNull(ReadOnlySpan<byte> bytes, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        Rlp.ValueDecoderContext context = new(bytes);
        return DecodeCompleteNotNull(ref context, rlpBehaviors);
    }

    NettyRlpStream EncodeToNewNettyStream(T? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        NettyRlpStream rlpStream;
        if (item is null)
        {
            rlpStream = new NettyRlpStream(NethermindBuffers.Default.Buffer(1));
            rlpStream.WriteByte(Rlp.EmptyListByte);
            return rlpStream;
        }

        rlpStream = new NettyRlpStream(NethermindBuffers.Default.Buffer(GetLength(item, rlpBehaviors)));
        Encode(rlpStream, item, rlpBehaviors);
        return rlpStream;
    }

    NettyRlpStream EncodeToNewNettyStream(T?[]? items, RlpBehaviors behaviors = RlpBehaviors.None)
    {
        NettyRlpStream rlpStream;
        if (items is null)
        {
            rlpStream = new NettyRlpStream(NethermindBuffers.Default.Buffer(1));
            rlpStream.WriteByte(Rlp.EmptyListByte);
            return rlpStream;
        }

        int totalLength = 0;
        for (int i = 0; i < items.Length; i++)
        {
            totalLength += GetLength(items[i], behaviors);
        }

        int bufferLength = Rlp.LengthOfSequence(totalLength);

        rlpStream = new NettyRlpStream(NethermindBuffers.Default.Buffer(bufferLength));
        rlpStream.StartSequence(totalLength);

        for (int i = 0; i < items.Length; i++)
        {
            Encode(rlpStream, items[i], behaviors);
        }

        return rlpStream;
    }

    NettyRlpStream EncodeToNewNettyStream(IList<T?>? items, RlpBehaviors behaviors = RlpBehaviors.None)
    {
        NettyRlpStream rlpStream;
        if (items is null)
        {
            rlpStream = new NettyRlpStream(NethermindBuffers.Default.Buffer(1));
            rlpStream.WriteByte(Rlp.EmptyListByte);
            return rlpStream;
        }

        int totalLength = 0;
        for (int i = 0; i < items.Count; i++)
        {
            totalLength += GetLength(items[i], behaviors);
        }

        int bufferLength = Rlp.LengthOfSequence(totalLength);

        rlpStream = new NettyRlpStream(NethermindBuffers.Default.Buffer(bufferLength));
        rlpStream.StartSequence(totalLength);

        for (int i = 0; i < items.Count; i++)
        {
            Encode(rlpStream, items[i], behaviors);
        }

        return rlpStream;
    }

    NettyRlpStream EncodeToNewNettyStream(in ArrayPoolListRef<T?> items, RlpBehaviors behaviors = RlpBehaviors.None)
    {
        int totalLength = 0;
        for (int i = 0; i < items.Count; i++)
        {
            totalLength += GetLength(items[i], behaviors);
        }

        int bufferLength = Rlp.LengthOfSequence(totalLength);

        NettyRlpStream rlpStream = new(NethermindBuffers.Default.Buffer(bufferLength));
        rlpStream.StartSequence(totalLength);

        for (int i = 0; i < items.Count; i++)
        {
            Encode(rlpStream, items[i], behaviors);
        }

        return rlpStream;
    }

    CappedArray<byte> EncodeToCappedArray(T? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None, ICappedArrayPool? bufferPool = null)
    {
        int size = GetLength(item, rlpBehaviors);
        CappedArray<byte> buffer = bufferPool.SafeRent(size);
        Encode(buffer.AsRlpStream(), item, rlpBehaviors);
        return buffer;
    }

    void Encode(RlpStream stream, T?[]? items, RlpBehaviors behaviors = RlpBehaviors.None)
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

    int GetContentLength(T?[]? items, RlpBehaviors behaviors = RlpBehaviors.None)
    {
        if (items is null)
        {
            return Rlp.OfEmptyList.Length;
        }

        int contentLength = 0;
        for (int i = 0; i < items.Length; i++)
        {
            contentLength += items[i] is null ? Rlp.OfEmptyList.Length : GetLength(items[i], behaviors);
        }

        return contentLength;
    }

    int GetLength(T?[]? items, RlpBehaviors behaviors = RlpBehaviors.None)
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
}
