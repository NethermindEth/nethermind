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
            if (items is [])
            {
                return Rlp.OfEmptyList;
            }

            byte[] bytes = new byte[RlpDecoderExtensions.GetLength(this, items, rlpBehaviors)];
            RlpDecoderExtensions.Encode(this, new RlpStream(bytes), items, rlpBehaviors);
            return new Rlp(bytes);
        }

        T Decode(ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None);
    }

    public static class RlpValueDecoderExtensions
    {
        extension<T>(IRlpDecoder<T> decoder)
        {
            public T Decode(ReadOnlySpan<byte> bytes, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
            {
                Rlp.ValueDecoderContext context = new(bytes);
                return decoder.Decode(ref context, rlpBehaviors);
            }

            /// <summary>
            /// Decodes instance of <typeparamref name="T"/> from <paramref name="context"/>
            /// and verifies that the end of the stream has been reached.
            /// </summary>
            public T DecodeComplete(ref Rlp.ValueDecoderContext context, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
            {
                T value = decoder.Decode(ref context, rlpBehaviors);
                context.CheckEnd();
                return value;
            }

            /// <summary>
            /// Decodes instance of <typeparamref name="T"/> from <paramref name="bytes"/>
            /// and verifies that the end of the stream has been reached.
            /// </summary>
            public T DecodeComplete(ReadOnlySpan<byte> bytes, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
            {
                Rlp.ValueDecoderContext context = new(bytes);
                return decoder.DecodeComplete(ref context, rlpBehaviors);
            }
        }

        extension<T>(IRlpDecoder<T> decoder) where T : class
        {
            public T DecodeGuardNotNull(ref Rlp.ValueDecoderContext context, RlpBehaviors rlpBehaviors = RlpBehaviors.None) =>
                decoder.Decode(ref context, rlpBehaviors) ?? ThrowNullDecodedValue<T>();

            public T DecodeGuardNotNull(ReadOnlySpan<byte> bytes, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
            {
                Rlp.ValueDecoderContext context = new(bytes);
                return decoder.DecodeGuardNotNull(ref context, rlpBehaviors);
            }

            /// <summary>
            /// Decodes instance of <typeparamref name="T"/> from <paramref name="context"/>
            /// and verifies that the end of the stream has been reached.
            /// Throws if decoded value is <c>null</c>.
            /// </summary>
            public T DecodeCompleteNotNull(ref Rlp.ValueDecoderContext context, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
            {
                T value = decoder.DecodeGuardNotNull(ref context, rlpBehaviors);
                context.CheckEnd();
                return value;
            }

            /// <summary>
            /// Decodes instance of <typeparamref name="T"/> from <paramref name="bytes"/>
            /// and verifies that the end of the stream has been reached.
            /// Throws if decoded value is <c>null</c>.
            /// </summary>
            public T DecodeCompleteNotNull(ReadOnlySpan<byte> bytes, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
            {
                Rlp.ValueDecoderContext context = new(bytes);
                return decoder.DecodeCompleteNotNull(ref context, rlpBehaviors);
            }
        }

        [DoesNotReturn]
        [StackTraceHidden]
        private static T ThrowNullDecodedValue<T>() where T : class
            => throw new RlpException($"{typeof(T).Name} decoding returned null");
    }

    public abstract class RlpValueDecoder<T> : IRlpDecoder<T>
    {
        public abstract int GetLength(T item, RlpBehaviors rlpBehaviors = RlpBehaviors.None);

        public abstract void Encode(RlpStream stream, T item, RlpBehaviors rlpBehaviors = RlpBehaviors.None);

        [DoesNotReturn]
        [StackTraceHidden]
        protected static void ThrowRlpException(Exception exception) =>
            throw new RlpException($"Cannot decode stream of {nameof(T)}", exception);

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
