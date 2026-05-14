// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

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

        byte[] bytes = new byte[RlpDecoderExtensions.GetLength(this, items, rlpBehaviors)];
        RlpDecoderExtensions.Encode(this, new RlpStream(bytes), items, rlpBehaviors);
        return new Rlp(bytes);
    }

    T Decode(ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None);

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

    [DoesNotReturn]
    [StackTraceHidden]
    private static T ThrowNullDecodedValue()
        => throw new RlpException($"{typeof(T).Name} decoding returned null");
}
