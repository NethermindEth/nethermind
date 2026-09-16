// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Numerics;
using System.Text;
using System.Text.Json;

using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.Abi
{
    public class AbiDynamicBytes : AbiType
    {
        public static readonly AbiDynamicBytes Instance = new();

        private AbiDynamicBytes()
        {
        }

        public override bool IsDynamic => true;

        public override string Name => "bytes";

        public override Type CSharpType { get; } = typeof(byte[]);

        public override (object, int) Decode(byte[] data, int position, bool packed)
        {
            using DecodeBudgetScope decodeBudget = EnterDecodeBudget(data.Length);
            (UInt256 length, int currentPosition) = UInt256.DecodeUInt(data, position, packed);
            int remainingDataLength = data.Length - currentPosition;
            if (length > (UInt256)remainingDataLength)
            {
                throw new AbiException($"Insufficient data to decode ABI {Name} of length {length} at position {currentPosition}");
            }

            int valueLength = (int)length;
            int encodedLength = packed ? valueLength : GetPaddingSize(valueLength);
            if (encodedLength > remainingDataLength)
            {
                throw new AbiException($"Insufficient data to decode padded ABI {Name} of length {length} at position {currentPosition}");
            }

            ConsumeDecodeBudget((uint)valueLength, this);
            return (data.Slice(currentPosition, valueLength), currentPosition + encodedLength);
        }

        public override byte[] Encode(object? arg, bool packed)
        {
            if (arg is byte[] input)
            {
                byte[] lengthEncoded = UInt256.Encode(new BigInteger(input.Length), packed);
                return Bytes.Concat(lengthEncoded, packed ? input : input.PadRight(GetPaddingSize(input.Length)));
            }

            if (arg is string stringInput)
            {
                return Encode(Encoding.ASCII.GetBytes(stringInput), packed);
            }

            if (arg is JsonElement element && element.ValueKind == JsonValueKind.String)
            {
                return Encode(Encoding.ASCII.GetBytes(element.GetString()!), packed);
            }

            throw new AbiException(AbiEncodingExceptionMessage);
        }

        private static int GetPaddingSize(int length)
        {
            int remainder = length % PaddingSize;
            int paddingSize = checked(length + (remainder == 0 ? 0 : (PaddingSize - remainder)));
            return paddingSize;
        }
    }
}
