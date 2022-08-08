//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
//
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
//
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
//
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

namespace Nethermind.Serialization.Rlp
{
    public static class RlpDecoderExtensions
    {
        public static T[] DecodeArray<T>(this IRlpStreamDecoder<T> decoder, RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            int checkPosition = rlpStream.ReadSequenceLength() + rlpStream.Position;
            T[] result = new T[rlpStream.ReadNumberOfItemsRemaining(checkPosition)];
            for (int i = 0; i < result.Length; i++)
            {
                result[i] = decoder.Decode(rlpStream, rlpBehaviors);
            }

            return result;
        }

        public static T[] DecodeArray<T>(this IRlpValueDecoder<T> decoder, ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            int checkPosition = decoderContext.ReadSequenceLength() + decoderContext.Position;
            T[] result = new T[decoderContext.ReadNumberOfItemsRemaining(checkPosition)];
            for (int i = 0; i < result.Length; i++)
            {
                result[i] = decoder.Decode(ref decoderContext, rlpBehaviors);
            }

            return result;
        }

        public static void Encode<T>(this IRlpStreamDecoder<T> decoder, RlpStream stream, T?[]? items, RlpBehaviors behaviors = RlpBehaviors.None)
        {
            if (items == null)
            {
                stream.Encode(Rlp.OfEmptySequence);
            }

            stream.StartSequence(decoder.GetContentLength(items, behaviors));
            for (int i = 0; i < items.Length; i++)
            {
                if (items[i] == null)
                {
                    stream.Encode(Rlp.OfEmptySequence);
                }
                else
                {
                    decoder.Encode(stream, items[i], behaviors);
                }
            }

        }

        public static int GetContentLength<T>(this IRlpStreamDecoder<T> decoder, T?[]? items, RlpBehaviors behaviors = RlpBehaviors.None)
        {
            if (items == null)
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
            if (items == null)
            {
                return Rlp.OfEmptySequence.Length;
            }

            return Rlp.LengthOfSequence(decoder.GetContentLength(items, behaviors));
        }
    }
}
