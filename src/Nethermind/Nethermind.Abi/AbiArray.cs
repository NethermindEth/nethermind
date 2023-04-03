// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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

        public override (object, int) Decode(byte[] data, int position, bool packed)
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
