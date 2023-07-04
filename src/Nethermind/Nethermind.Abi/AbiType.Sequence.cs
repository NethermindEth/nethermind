// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Numerics;
using Nethermind.Int256;

namespace Nethermind.Abi
{
    public partial class AbiType
    {
        protected const int PaddingSize = 32;

        internal static byte[][] EncodeSequence(int length, IEnumerable<AbiType> types, IEnumerable<object?> sequence, bool packed, int offset = 0)
        {
            List<byte[]> dynamicParts = new(length);
            List<byte[]?> headerParts = new(length);
            using IEnumerator<object?> sequenceEnumerator = sequence.GetEnumerator();
            using IEnumerator<AbiType> typesEnumerator = types.GetEnumerator();
            for (int i = 0; i < length; i++)
            {
                sequenceEnumerator.MoveNext();
                typesEnumerator.MoveNext();
                object? element = sequenceEnumerator.Current;
                AbiType type = typesEnumerator.Current;

                byte[] encoded = type.Encode(element, packed);

                // encode each type
                if (type.IsDynamic)
                {
                    // offset placeholder, we cannot calculate offset before calculating all header parts
                    headerParts.Add(null);
                    dynamicParts.Add(encoded);
                }
                else
                {
                    headerParts.Add(encoded);
                }
            }

            // now lets calculate proper offset
            BigInteger currentOffset = 0;

            // offset of header
            for (int i = 0; i < headerParts.Count; i++)
            {
                currentOffset += headerParts[i]?.Length ?? PaddingSize;
            }

            // offset dynamic parts, calculating the actual offset of each part
            int dynamicPartsIndex = 0;
            for (int i = 0; i < headerParts.Count; i++)
            {
                if (headerParts[i] is null)
                {
                    headerParts[i] = UInt256.Encode(currentOffset, packed);
                    currentOffset += dynamicParts[dynamicPartsIndex++].Length;
                }
            }

            byte[][] encodedParts = new byte[offset + headerParts.Count + dynamicParts.Count][];

            for (int i = 0; i < headerParts.Count; i++)
            {
                encodedParts[offset + i] = headerParts[i]!;
            }

            for (int i = 0; i < dynamicParts.Count; i++)
            {
                encodedParts[offset + headerParts.Count + i] = dynamicParts[i];
            }

            return encodedParts;
        }

        internal static (object[], int) DecodeSequence(int length, IEnumerable<AbiType> types, byte[] data, bool packed, int startPosition)
        {
            (Array array, int position) = DecodeSequence(typeof(object), length, types, data, packed, startPosition);
            return ((object[])array, position);
        }

        internal static (Array, int) DecodeSequence(Type elementType, int length, IEnumerable<AbiType> types, byte[] data, bool packed, int startPosition)
        {
            Array sequence = Array.CreateInstance(elementType, length);
            int position = startPosition;
            int dynamicPosition = 0;
            using IEnumerator<AbiType> typesEnumerator = types.GetEnumerator();
            object? item;
            for (int i = 0; i < length; i++)
            {
                typesEnumerator.MoveNext();
                AbiType type = typesEnumerator.Current;

                if (type.IsDynamic)
                {
                    (UInt256 offset, int nextPosition) = UInt256.DecodeUInt(data, position, packed);
                    (item, dynamicPosition) = type.Decode(data, startPosition + (int)offset, packed);
                    position = nextPosition;
                }
                else
                {
                    (item, position) = type.Decode(data, position, packed);
                }

                sequence.SetValue(item, i);
            }

            return (sequence, Math.Max(position, dynamicPosition));
        }
    }
}
