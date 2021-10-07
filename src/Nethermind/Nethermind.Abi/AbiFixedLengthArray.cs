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
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Nethermind.Core.Extensions;

namespace Nethermind.Abi
{
    public class AbiFixedLengthArray : AbiType
    {
        public AbiType ElementType { get; }

        public AbiFixedLengthArray(AbiType elementType, int length)
        {
            if (length <= 0)
            {
                throw new ArgumentException($"Length of {nameof(AbiFixedLengthArray)} has to be greater than 0", nameof(length));
            }

            ElementType = elementType;
            Length = length;
            Name =  $"{ElementType}[{Length}]";
            CSharpType = ElementType.CSharpType.MakeArrayType();
            IsDynamic = Length != 0 && ElementType.IsDynamic;
        }

        public override bool IsDynamic { get; }

        public int Length { get; }

        public override string Name { get; }

        public override (object, int) Decode(Memory<byte> data, int position, bool packed) => 
            DecodeSequence(ElementType.CSharpType, Length, ElementTypes, data, packed, position);

        public override byte[] Encode(object? arg, bool packed)
        {
            if (arg is Array input)
            {
                if (input.Length != Length)
                {
                    throw new AbiException(AbiEncodingExceptionMessage);
                }

                byte[][] encodedItems = EncodeSequence(input.Length, ElementTypes, input.Cast<object?>(), packed);
                return Bytes.Concat(encodedItems);
            }

            throw new AbiException(AbiEncodingExceptionMessage);
        }

        public override Type CSharpType { get; }
        
        private IEnumerable<AbiType> ElementTypes
        {
            get
            {
                yield return ElementType;
            }
        }
    }
}
