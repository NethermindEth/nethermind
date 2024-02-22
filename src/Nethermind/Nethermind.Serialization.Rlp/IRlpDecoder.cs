// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;

namespace Nethermind.Serialization.Rlp
{
    public interface IRlpDecoder
    {
    }

    public interface IRlpDecoder<in T> : IRlpDecoder
    {
        int GetLength(T item, RlpBehaviors rlpBehaviors);
    }

    public interface IRlpStreamDecoder<T> : IRlpDecoder<T>
    {
        T Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None);
        void Encode(RlpStream stream, T item, RlpBehaviors rlpBehaviors = RlpBehaviors.None);
    }

    public interface IRlpObjectDecoder<in T> : IRlpDecoder<T>
    {
        Rlp Encode(T? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None);
    }

    public interface IRlpValueDecoder<T> : IRlpDecoder<T>
    {
        T Decode(ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None);
    }

    public readonly struct RlpValueDecoderSpanDeserializer<T, TRlpValueDecoder>(TRlpValueDecoder valueDecoder) : ISpanDeserializer<T>
        where TRlpValueDecoder : IRlpValueDecoder<T>
    {
        public T Deserialize(ReadOnlySpan<byte> span)
        {
            var decoderContext = new Rlp.ValueDecoderContext(span);
            return valueDecoder.Decode(ref decoderContext);
        }
    }

    public static class RlpValueDecoderExtensions
    {
        public static T Decode<T>(this IRlpValueDecoder<T> decoder, ReadOnlySpan<byte> bytes, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            Rlp.ValueDecoderContext context = new(bytes);
            return decoder.Decode(ref context, rlpBehaviors);
        }
    }
}
