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
// 

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
            int index = 0;
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

                try
                {
                    sequence.SetValue(item, i);
                }
                catch (InvalidCastException e)
                {
                    throw;
                }
            }

            return (sequence, Math.Max(position, dynamicPosition));
        }
    }
}
