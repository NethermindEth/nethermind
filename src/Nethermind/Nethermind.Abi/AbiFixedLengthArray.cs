// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
            Name = $"{ElementType}[{Length}]";
            CSharpType = ElementType.CSharpType.MakeArrayType();
            IsDynamic = Length != 0 && ElementType.IsDynamic;
        }

        public override bool IsDynamic { get; }

        public int Length { get; }

        public override string Name { get; }

        public override (object, int) Decode(byte[] data, int position, bool packed) =>
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
