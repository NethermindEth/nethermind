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
using Nethermind.Int256;

namespace Nethermind.Abi
{
    public class AbiArray : AbiType
    {
        public AbiType ElementType { get; }

        public AbiArray(AbiType elementType)
        {
            ElementType = elementType;
            Name = $"{ElementType}[]";
            CSharpType = ElementType.CSharpType.MakeArrayType();
        }

        public override bool IsDynamic => true;

        public override string Name { get; }

        public override Type CSharpType { get; }

        public override (object, int) Decode(Memory<byte> data, int position, bool packed)
        {
            UInt256 length;
            (length, position) = UInt256.DecodeUInt(data, position, packed);
            return DecodeSequence(ElementType.CSharpType, (int)length, ElementTypes, data, packed, position);
        }

        public override byte[] Encode(object? arg, bool packed)
        {
            if (arg is Array input)
            {
                byte[][] encodedItems = EncodeSequence(input.Length, ElementTypes, input.Cast<object?>(), packed, 1);
                encodedItems[0] = UInt256.Encode((BigInteger)input.Length, packed);
                return Bytes.Concat(encodedItems);
            }

            throw new AbiException(AbiEncodingExceptionMessage);
        }

        private IEnumerable<AbiType> ElementTypes
        {
            get
            {
                yield return ElementType;
            }
        }
    }
}
