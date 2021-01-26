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
using System.Text;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.Abi
{
    public class AbiBytes : AbiType
    {
        private const int MaxLength = 32;
        private const int MinLength = 0;

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
        }

        public int Length { get; }

        public override string Name => $"bytes{Length}";

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
