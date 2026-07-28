// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.Abi;

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
        (UInt256 length, position) = UInt256.DecodeUInt(data, position, packed);
        int sequenceLength = (int)length;
        int elementHeadSize = GetElementHeadSize(ElementType, packed);
        int allocationBoundElementSize = Math.Max(elementHeadSize, 1);

        if ((uint)position > (uint)data.Length)
        {
            throw new AbiException($"Insufficient data to decode ABI array of {sequenceLength} elements at position {position}");
        }

        int allocationBoundDataLength = elementHeadSize == 0 ? data.Length : data.Length - position;
        if ((ulong)(uint)sequenceLength * (uint)allocationBoundElementSize > (uint)allocationBoundDataLength)
        {
            throw new AbiException($"Insufficient data to decode ABI array of {sequenceLength} elements at position {position}");
        }

        return DecodeSequence(ElementType.CSharpType, sequenceLength, ElementTypes, data, packed, position);
    }

    public override byte[] Encode(object? arg, bool packed)
    {
        int length;
        byte[][] encodedItems;
        switch (arg)
        {
            case Array array:
                length = array.Length;
                encodedItems = EncodeSequence(length, ElementTypes, array.Cast<object?>(), packed, 1);
                break;
            case IList list:
                length = list.Count;
                encodedItems = EncodeSequence(length, ElementTypes, list.Cast<object?>(), packed, 1);
                break;
            case JsonElement element when element.ValueKind == JsonValueKind.Array:
                length = element.GetArrayLength();
                object[] jArray = new object[length];
                for (int i = 0; i < length; i++)
                {
                    jArray[i] = element[i];
                }
                encodedItems = EncodeSequence(length, ElementTypes, jArray, packed, 1);
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

    private static int GetElementHeadSize(AbiType type, bool packed)
    {
        if (type.IsDynamic)
        {
            return PaddingSize;
        }

        if (type.ComponentTypes is { } componentTypes)
        {
            int tupleHeadSize = 0;
            for (int i = 0; i < componentTypes.Count; i++)
            {
                tupleHeadSize = checked(tupleHeadSize + GetElementHeadSize(componentTypes[i], packed));
            }

            return tupleHeadSize;
        }

        if (type is AbiFixedLengthArray array)
        {
            return checked(array.Length * GetElementHeadSize(array.ElementType, packed));
        }

        if (!packed)
        {
            return PaddingSize;
        }

        return type switch
        {
            AbiBytes bytes => bytes.Length,
            AbiInt abiInt => abiInt.LengthInBytes,
            AbiUInt abiUInt => abiUInt.LengthInBytes,
            AbiFixed => PaddingSize,
            AbiUFixed => PaddingSize,
            _ => 1,
        };
    }
}
