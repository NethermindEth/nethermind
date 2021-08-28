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
        
        internal static byte[][] EncodeSequence(AbiType[] types, IEnumerable<object?> sequence, bool packed, int offset = 0)
        {
            List<byte[]> dynamicParts = new(types.Length);
            List<byte[]?> headerParts = new(types.Length);
            int index = 0;
            foreach (object? argument in sequence)
            {
                AbiType type = types[index++];
                byte[] encoded = type.Encode(argument, packed);
                
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
        
        internal static (object[], int) DecodeSequence(AbiType[] types, byte[] data, bool packed, int startPosition)
        {
            object[] sequence = new object[types.Length];
            int position = startPosition;
            int dynamicPosition = 0;
            for (int i = 0; i < types.Length; i++)
            {
                AbiType type = types[i];
                if (type.IsDynamic)
                {
                    (UInt256 offset, int nextPosition) = UInt256.DecodeUInt(data, position, packed);
                    (sequence[i], dynamicPosition) = type.Decode(data, startPosition + (int)offset, packed);
                    position = nextPosition;
                }
                else
                {
                    (sequence[i], position) = type.Decode(data, position, packed);
                }
            }

            return (sequence, Math.Max(position, dynamicPosition));
        }
    }
}
