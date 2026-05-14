// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;

namespace Nethermind.Serialization.Rlp
{
    public static class RlpDecoderExtensions
    {
        private static readonly CappedArray<byte>[] s_intPreEncodes = CreatePreEncodes();

        public static T[] DecodeArray<T>(this IRlpDecoder<T> decoder, ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None, RlpLimit? limit = null)
        {
            int checkPosition = decoderContext.ReadSequenceLength() + decoderContext.Position;
            int length = decoderContext.PeekNumberOfItemsRemaining(checkPosition);
            decoderContext.GuardLimit(length, limit);
            T[] result = new T[length];
            for (int i = 0; i < result.Length; i++)
            {
                result[i] = decoder.Decode(ref decoderContext, rlpBehaviors);
            }

            if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) != RlpBehaviors.AllowExtraBytes)
            {
                decoderContext.Check(checkPosition);
            }

            return result;
        }

        public static NettyRlpStream EncodeToNewNettyStream<T>(this IRlpDecoder<T> decoder, T? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            NettyRlpStream rlpStream;
            if (item is null)
            {
                rlpStream = new NettyRlpStream(NethermindBuffers.Default.Buffer(1));
                rlpStream.WriteByte(Rlp.EmptyListByte);
                return rlpStream;
            }

            rlpStream = new NettyRlpStream(NethermindBuffers.Default.Buffer(decoder.GetLength(item, rlpBehaviors)));
            decoder.Encode(rlpStream, item, rlpBehaviors);
            return rlpStream;
        }

        public static NettyRlpStream EncodeToNewNettyStream<T>(this IRlpDecoder<T> decoder, T?[]? items, RlpBehaviors behaviors = RlpBehaviors.None)
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

        public static NettyRlpStream EncodeToNewNettyStream<T>(this IRlpDecoder<T> decoder, IList<T?>? items, RlpBehaviors behaviors = RlpBehaviors.None)
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

        public static NettyRlpStream EncodeToNewNettyStream<T>(this IRlpDecoder<T> decoder, in ArrayPoolListRef<T?> items, RlpBehaviors behaviors = RlpBehaviors.None)
        {
            int totalLength = 0;
            for (int i = 0; i < items.Count; i++)
            {
                totalLength += decoder.GetLength(items[i], behaviors);
            }

            int bufferLength = Rlp.LengthOfSequence(totalLength);

            NettyRlpStream rlpStream = new(NethermindBuffers.Default.Buffer(bufferLength));
            rlpStream.StartSequence(totalLength);

            for (int i = 0; i < items.Count; i++)
            {
                decoder.Encode(rlpStream, items[i], behaviors);
            }

            return rlpStream;
        }

        public static CappedArray<byte> EncodeToCappedArray<T>(this IRlpDecoder<T> decoder, T? item,
            RlpBehaviors rlpBehaviors = RlpBehaviors.None, ICappedArrayPool? bufferPool = null)
        {
            int size = decoder.GetLength(item, rlpBehaviors);
            CappedArray<byte> buffer = bufferPool.SafeRent(size);
            decoder.Encode(buffer.AsRlpStream(), item, rlpBehaviors);
            return buffer;
        }

        public static CappedArray<byte> EncodeToCappedArray(this int item, ICappedArrayPool? bufferPool = null)
        {
            CappedArray<byte>[] cache = s_intPreEncodes;
            if ((uint)item < (uint)cache.Length)
            {
                return cache[item];
            }

            CappedArray<byte> buffer = bufferPool.SafeRent(Rlp.LengthOf(item));
            buffer.AsRlpStream().Encode(item);
            return buffer;
        }

        public static void Encode<T>(this IRlpDecoder<T> decoder, RlpStream stream, T?[]? items, RlpBehaviors behaviors = RlpBehaviors.None)
        {
            if (items is null)
            {
                stream.Encode(Rlp.OfEmptyList);
                return;
            }

            stream.StartSequence(decoder.GetContentLength(items, behaviors));
            for (int index = 0; index < items.Length; index++)
            {
                T t = items[index];
                if (t is null)
                {
                    stream.Encode(Rlp.OfEmptyList);
                }
                else
                {
                    decoder.Encode(stream, t, behaviors);
                }
            }

        }

        public static int GetContentLength<T>(this IRlpDecoder<T> decoder, T?[]? items, RlpBehaviors behaviors = RlpBehaviors.None)
        {
            if (items is null)
            {
                return Rlp.OfEmptyList.Length;
            }

            int contentLength = 0;
            for (int i = 0; i < items.Length; i++)
            {
                contentLength += items[i] is null ? Rlp.OfEmptyList.Length : decoder.GetLength(items[i], behaviors);
            }

            return contentLength;
        }

        public static int GetLength<T>(this IRlpDecoder<T> decoder, T?[]? items, RlpBehaviors behaviors = RlpBehaviors.None)
        {
            if (items is null)
            {
                return Rlp.OfEmptyList.Length;
            }

            return Rlp.LengthOfSequence(decoder.GetContentLength(items, behaviors));
        }

        private static CappedArray<byte>[] CreatePreEncodes()
        {
            const int MaxCache = 1024;

            CappedArray<byte>[] cache = new CappedArray<byte>[MaxCache];

            for (int i = 0; i < cache.Length; i++)
            {
                int size = Rlp.LengthOf(i);
                byte[] buffer = new byte[size];
                buffer.AsRlpStream().Encode(i);
                cache[i] = new CappedArray<byte>(buffer);
            }

            return cache;
        }
    }
}
