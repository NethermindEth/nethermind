// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Numerics;
using System.Text;
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
            (UInt256 length, int currentPosition) = UInt256.DecodeUInt(data, position, packed);
            int paddingSize = packed ? (int)length : GetPaddingSize((int)length);
            return (data.Slice(currentPosition, (int)length), currentPosition + paddingSize);
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

            throw new AbiException(AbiEncodingExceptionMessage);
        }

        private static int GetPaddingSize(int length)
        {
            int remainder = length % PaddingSize;
            int paddingSize = length + (remainder == 0 ? 0 : (PaddingSize - remainder));
            return paddingSize;
        }
    }
}
