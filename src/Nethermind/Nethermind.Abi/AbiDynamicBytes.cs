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
using System.Numerics;
using System.Text;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.Abi
{
    public class AbiDynamicBytes : AbiType
    {
        private const int PaddingMultiple = 32;

        public static AbiDynamicBytes Instance = new();

        private AbiDynamicBytes()
        {
        }

        public override bool IsDynamic => true;

        public override string Name => "bytes";

        public override Type CSharpType { get; } = typeof(byte[]);

        public override (object, int) Decode(byte[] data, int position, bool packed)
        {
            (UInt256 length, int currentPosition) = UInt256.DecodeUInt(data, position, packed);
            int paddingSize = packed ? (int)length : (1 + (int) length / PaddingMultiple) * PaddingMultiple;
            return (data.Slice(currentPosition, (int) length), currentPosition + paddingSize);
        }

        public override byte[] Encode(object? arg, bool packed)
        {
            if (arg is byte[] input)
            {
                int remainder = input.Length % PaddingMultiple;
                int paddingSize = input.Length + (remainder == 0 ? 0 : (PaddingMultiple - remainder));

                byte[] lengthEncoded = UInt256.Encode(new BigInteger(input.Length), packed);
                return Bytes.Concat(lengthEncoded, packed ? input : input.PadRight(paddingSize));
            }

            if (arg is string stringInput)
            {
                return Encode(Encoding.ASCII.GetBytes(stringInput), packed);
            }

            throw new AbiException(AbiEncodingExceptionMessage);
        }
    }
}
