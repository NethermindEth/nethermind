// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
    }
}
