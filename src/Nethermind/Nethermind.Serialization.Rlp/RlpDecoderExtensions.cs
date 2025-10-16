// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core.Buffers;

namespace Nethermind.Serialization.Rlp
{
    public static class RlpDecoderExtensions
    {
        private static readonly SpanSource[] s_intPreEncodes = CreatePreEncodes();

        public static T[] DecodeArray<T>(this IRlpStreamDecoder<T> decoder, RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None, RlpLimit? limit = null)
        {
            int checkPosition = rlpStream.ReadSequenceLength() + rlpStream.Position;
            int length = rlpStream.PeekNumberOfItemsRemaining(checkPosition);
            Rlp.GuardLimit(length, limit);
            T[] result = new T[length];
            for (int i = 0; i < result.Length; i++)
            {
                result[i] = decoder.Decode(rlpStream, rlpBehaviors);
            }

            return result;
        }

        public static T[] DecodeArray<T>(this IRlpValueDecoder<T> decoder, ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None, RlpLimit? limit = null)
        {
            int checkPosition = decoderContext.ReadSequenceLength() + decoderContext.Position;
            int length = decoderContext.PeekNumberOfItemsRemaining(checkPosition);
            Rlp.GuardLimit(length, limit);
            T[] result = new T[length];
            for (int i = 0; i < result.Length; i++)
            {
                result[i] = decoder.Decode(ref decoderContext, rlpBehaviors);
            }

            return result;
        }

        public static Rlp Encode<T>(this IRlpObjectDecoder<T> decoder, T?[]? items, RlpBehaviors behaviors = RlpBehaviors.None)
        {
            if (items is null)
            {
                return Rlp.OfEmptySequence;
            }

            Rlp[] rlpSequence = new Rlp[items.Length];
            for (int i = 0; i < items.Length; i++)
            {
                rlpSequence[i] = items[i] is null ? Rlp.OfEmptySequence : decoder.Encode(items[i], behaviors);
            }

            return Rlp.Encode(rlpSequence);
        }

        public static NettyRlpStream EncodeToNewNettyStream<T>(this IRlpStreamDecoder<T> decoder, T? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            NettyRlpStream rlpStream;
            if (item is null)
            {
                rlpStream = new NettyRlpStream(NethermindBuffers.Default.Buffer(1));
                rlpStream.WriteByte(Rlp.NullObjectByte);
                return rlpStream;
            }

            rlpStream = new NettyRlpStream(NethermindBuffers.Default.Buffer(decoder.GetLength(item, rlpBehaviors)));
            decoder.Encode(rlpStream, item, rlpBehaviors);
            return rlpStream;
        }

        public static NettyRlpStream EncodeToNewNettyStream<T>(this IRlpStreamDecoder<T> decoder, T?[]? items, RlpBehaviors behaviors = RlpBehaviors.None)
        {
            NettyRlpStream rlpStream;
            if (items is null)
            {
                rlpStream = new NettyRlpStream(NethermindBuffers.Default.Buffer(1));
                rlpStream.WriteByte(Rlp.NullObjectByte);
                return rlpStream;
            }

            int totalLength = 0;
            for (int i = 0; i < items.Length; i++)
            {
                totalLength += decoder.GetLength(items[i], behaviors);
            }

            int bufferLength = Rlp.LengthOfSequence(totalLength);

            rlpStream = new NettyRlpStream(NethermindBuffers.Default.Buffer(bufferLength));
            rlpStream.StartSequence(totalLength);

            for (int i = 0; i < items.Length; i++)
            {
                decoder.Encode(rlpStream, items[i], behaviors);
            }

            return rlpStream;
        }

        public static NettyRlpStream EncodeToNewNettyStream<T>(this IRlpStreamDecoder<T> decoder, IList<T?>? items, RlpBehaviors behaviors = RlpBehaviors.None)
        {
            NettyRlpStream rlpStream;
            if (items is null)
            {
                rlpStream = new NettyRlpStream(NethermindBuffers.Default.Buffer(1));
                rlpStream.WriteByte(Rlp.NullObjectByte);
                return rlpStream;
            }

            int totalLength = 0;
            for (int i = 0; i < items.Count; i++)
            {
                totalLength += decoder.GetLength(items[i], behaviors);
            }

            int bufferLength = Rlp.LengthOfSequence(totalLength);

            rlpStream = new NettyRlpStream(NethermindBuffers.Default.Buffer(bufferLength));
            rlpStream.StartSequence(totalLength);

            for (int i = 0; i < items.Count; i++)
            {
                decoder.Encode(rlpStream, items[i], behaviors);
            }

            return rlpStream;
        }

        public static SpanSource EncodeToSpanSource<T>(this IRlpStreamDecoder<T> decoder, T? item,
            RlpBehaviors rlpBehaviors = RlpBehaviors.None, ICappedArrayPool? bufferPool = null)
        {
            int size = decoder.GetLength(item, rlpBehaviors);
            SpanSource buffer = bufferPool.SafeRentBuffer(size);

            if (buffer.TryGetCappedArray(out CappedArray<byte> capped))
            {
                // This should be the usual path as this method is used only for encoding big RLP objects,
                // that should be pooled.
                decoder.Encode(capped.AsRlpStream(), item, rlpBehaviors);
            }
            else
            {
                ThrowSpanSourceNotCappedArray();
            }

            return buffer;
        }

        public static SpanSource EncodeToSpanSource(this int item, ICappedArrayPool? bufferPool = null)
        {
            SpanSource[] cache = s_intPreEncodes;
            if ((uint)item < (uint)cache.Length)
            {
                return cache[item];
            }

            SpanSource buffer = bufferPool.SafeRentBuffer(Rlp.LengthOf(item));

            if (buffer.TryGetCappedArray(out CappedArray<byte> capped))
            {
                // This should be the usual path as this method is used only for encoding big RLP objects,
                // that should be pooled.
                capped.AsRlpStream().Encode(item);
            }
            else
            {
                ThrowSpanSourceNotCappedArray();
            }

            return buffer;
        }

        [DoesNotReturn, StackTraceHidden]
        private static void ThrowSpanSourceNotCappedArray() => throw new InvalidOperationException("Encode to SpanSource failed to get a CappedArray.");

        public static Rlp Encode<T>(this IRlpObjectDecoder<T> decoder, IReadOnlyCollection<T?>? items, RlpBehaviors behaviors = RlpBehaviors.None)
        {
            if (items is null)
            {
                return Rlp.OfEmptySequence;
            }

            Rlp[] rlpSequence = new Rlp[items.Count];
            int i = 0;
            foreach (T? item in items)
            {
                rlpSequence[i++] = item is null ? Rlp.OfEmptySequence : decoder.Encode(item, behaviors);
            }

            return Rlp.Encode(rlpSequence);
        }

        public static void Encode<T>(this IRlpStreamDecoder<T> decoder, RlpStream stream, T?[]? items, RlpBehaviors behaviors = RlpBehaviors.None)
        {
            if (items is null)
            {
                stream.Encode(Rlp.OfEmptySequence);
            }

            stream.StartSequence(decoder.GetContentLength(items, behaviors));
            for (int index = 0; index < items.Length; index++)
            {
                T t = items[index];
                if (t is null)
                {
                    stream.Encode(Rlp.OfEmptySequence);
                }
                else
                {
                    decoder.Encode(stream, t, behaviors);
                }
            }

        }

        public static int GetContentLength<T>(this IRlpStreamDecoder<T> decoder, T?[]? items, RlpBehaviors behaviors = RlpBehaviors.None)
        {
            if (items is null)
            {
                return Rlp.OfEmptySequence.Length;
            }

            int contentLength = 0;
            for (int i = 0; i < items.Length; i++)
            {
                contentLength += decoder.GetLength(items[i], behaviors);
            }

            return contentLength;
        }

        public static int GetLength<T>(this IRlpStreamDecoder<T> decoder, T?[]? items, RlpBehaviors behaviors = RlpBehaviors.None)
        {
            if (items is null)
            {
                return Rlp.OfEmptySequence.Length;
            }

            return Rlp.LengthOfSequence(decoder.GetContentLength(items, behaviors));
        }

        private static SpanSource[] CreatePreEncodes()
        {
            const int MaxCache = 1024;

            SpanSource[] cache = new SpanSource[MaxCache];

            for (int i = 0; i < cache.Length; i++)
            {
                int size = Rlp.LengthOf(i);
                var buffer = new byte[size];
                buffer.AsRlpStream().Encode(i);
                cache[i] = new SpanSource(buffer);
            }

            return cache;
        }
    }
}
