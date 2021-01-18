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

using System;
using System.Numerics;
using Nethermind.Core.Extensions;

namespace Nethermind.Abi
{
    public class AbiFixedLengthArray : AbiType
    {
        private readonly AbiType _elementType;

        public AbiFixedLengthArray(AbiType elementType, int length)
        {
            if (length <= 0)
            {
                throw new ArgumentException($"Length of {nameof(AbiFixedLengthArray)} has to be greater than 0", nameof(length));
            }

            _elementType = elementType;
            Length = length;
            CSharpType = _elementType.CSharpType.MakeArrayType();
        }

        public override bool IsDynamic => Length != 0 && _elementType.IsDynamic;

        public int Length { get; }

        public override string Name => $"{_elementType}[{Length}]";

        public override (object, int) Decode(byte[] data, int position, bool packed)
        {
            Array result = Array.CreateInstance(_elementType.CSharpType, Length);

            if (_elementType.IsDynamic)
            {
                position += (Length - 1) * UInt256.LengthInBytes;
            }

            for (int i = 0; i < Length; i++)
            {
                (object element, int newPosition) = _elementType.Decode(data, position, packed);
                result.SetValue(element, i);
                position = newPosition;
            }

            return (result, position);
        }

        public override byte[] Encode(object? arg, bool packed)
        {
            if (arg is Array input)
            {
                if (input.Length != Length)
                {
                    throw new AbiException(AbiEncodingExceptionMessage);
                }

                if (_elementType.IsDynamic)
                {
                    byte[][] encodedItems = new byte[Length * 2 - 1][];
                    BigInteger currentOffset = (Length - 1) * UInt256.LengthInBytes;
                    int i = 0;
                    foreach (object? o in input)
                    {
                        encodedItems[Length + i - 1] = _elementType.Encode(o, packed);
                        if (i != 0)
                        {
                            encodedItems[i - 1] = UInt256.Encode(currentOffset, packed);
                            currentOffset += new BigInteger(encodedItems[Length + i - 1].Length);
                        }

                        i++;
                    }

                    return Bytes.Concat(encodedItems);
                }
                else
                {
                    byte[][] encodedItems = new byte[Length][];
                    int i = 0;
                    foreach (object? o in input)
                    {
                        encodedItems[i++] = _elementType.Encode(o, packed);
                    }

                    return Bytes.Concat(encodedItems);
                }
            }

            throw new AbiException(AbiEncodingExceptionMessage);
        }

        public override Type CSharpType { get; }
    }
}
