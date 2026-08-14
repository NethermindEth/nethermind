// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
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

        internal override int GetHeadSize(bool packed) =>
            IsDynamic ? PaddingSize : GetRepeatedHeadSize(Length, ElementType.GetHeadSize(packed));

        public override string Name { get; }

        public override (object, int) Decode(byte[] data, int position, bool packed)
        {
            using DecodeBudgetScope decodeBudget = EnterDecodeBudget(data.Length);
            int elementHeadSize = ElementType.GetHeadSize(packed);
            if (elementHeadSize is 0)
            {
                ConsumeZeroWidthElementBudget(Length, ElementType);
            }
            else
            {
                if ((uint)position > (uint)data.Length)
                {
                    throw new AbiException($"Insufficient data to decode ABI {Name} at position {position}");
                }

                int remainingDataLength = data.Length - position;
                ulong headSize = (ulong)(uint)Length * (uint)elementHeadSize;
                if (headSize > (uint)remainingDataLength)
                {
                    throw new AbiException(
                        $"ABI {Name} requires a head of {headSize} bytes, but only {remainingDataLength} bytes are available");
                }

                if (IsDynamic)
                {
                    ConsumeDecodeBudget(headSize, this);
                }
            }

            return DecodeSequence(ElementType.CSharpType, Length, ElementTypes, data, packed, position);
        }

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
