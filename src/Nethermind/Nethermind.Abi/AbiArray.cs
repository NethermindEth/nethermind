// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
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
            int length;
            byte[][] encodedItems;
            switch (arg)
            {
                case Array array:
                    length = array.Length;
                    encodedItems = EncodeSequence(array.Length, ElementTypes, array.Cast<object?>(), packed, 1);
                    break;
                case IList list:
                    length = list.Count;
                    encodedItems = EncodeSequence(list.Count, ElementTypes, list.Cast<object?>(), packed, 1);
                    break;
                default:
                    throw new AbiException(AbiEncodingExceptionMessage);
            }

            encodedItems[0] = UInt256.Encode((BigInteger)length, packed);
            return Bytes.Concat(encodedItems);
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
