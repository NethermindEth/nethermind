// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Nethermind.Serialization.Rlp
{
    public interface IRlpDecoder
    {
    }

    public interface IRlpDecoder<in T> : IRlpDecoder
    {
        int GetLength(T item, RlpBehaviors rlpBehaviors = RlpBehaviors.None);
    }

    public interface IRlpStreamEncoder<T> : IRlpDecoder<T>
    {
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

    public static class RlpValueDecoderExtensions
    {
        public static T Decode<T>(this IRlpValueDecoder<T> decoder, ReadOnlySpan<byte> bytes, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            Rlp.ValueDecoderContext context = new(bytes);
            return decoder.Decode(ref context, rlpBehaviors);
        }
    }

    public abstract class RlpStreamEncoder<T> : IRlpStreamEncoder<T>
    {
        public abstract int GetLength(T item, RlpBehaviors rlpBehaviors = RlpBehaviors.None);

        public abstract void Encode(RlpStream stream, T item, RlpBehaviors rlpBehaviors = RlpBehaviors.None);

        [DoesNotReturn]
        [StackTraceHidden]
        protected static void ThrowRlpException(Exception exception) =>
            throw new RlpException($"Cannot decode stream of {nameof(T)}", exception);
    }

    public abstract class RlpValueDecoder<T> : RlpStreamEncoder<T>, IRlpValueDecoder<T>
    {
        public T Decode(ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
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

        protected abstract T DecodeInternal(ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None);
    }
}
