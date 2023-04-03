// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.Abi
{
    public class AbiBytes : AbiType
    {
        private const int MaxLength = 32;
        private const int MinLength = 0;

        public static new AbiBytes Bytes32 { get; } = new(32);

        public AbiBytes(int length)
        {
            if (length > MaxLength)
            {
                throw new ArgumentException(nameof(length),
                    $"{nameof(length)} of {nameof(AbiBytes)} has to be less or equal to {MaxLength}");
            }

            if (length <= MinLength)
            {
                throw new ArgumentException(nameof(length),
                    $"{nameof(length)} of {nameof(AbiBytes)} has to be greater than {MinLength}");
            }

            Length = length;
            Name = $"bytes{Length}";
        }

        public int Length { get; }

        public override string Name { get; }

        public override (object, int) Decode(byte[] data, int position, bool packed)
        {
            return (data.Slice(position, Length), position + (packed ? Length : MaxLength));
        }

        public override byte[] Encode(object? arg, bool packed)
        {
            if (arg is byte[] input)
            {
                if (input.Length != Length)
                {
                    throw new AbiException(AbiEncodingExceptionMessage);
                }

                return input.PadRight(packed ? Length : MaxLength);
            }

            if (arg is string stringInput)
            {
                return Encode(Encoding.ASCII.GetBytes(stringInput), packed);
            }

            if (arg is Keccak hash && Length == 32)
            {
                return Encode(hash.Bytes, packed);
            }

            throw new AbiException(AbiEncodingExceptionMessage);
        }

        public override Type CSharpType { get; } = typeof(byte[]);
    }
}
