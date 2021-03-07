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
using Nethermind.Core;
using Nethermind.Core.Extensions;

namespace Nethermind.Abi
{
    public class AbiAddress : AbiUInt
    {
        private AbiAddress() : base(160)
        {
        }

        public static AbiAddress Instance { get; } = new();

        public override string Name => "address";

        public override byte[] Encode(object? arg, bool packed)
        {
            while (true)
            {
                switch (arg)
                {
                    case Address input:
                    {
                        byte[] bytes = input.Bytes;
                        return packed ? bytes : bytes.PadLeft(UInt256.LengthInBytes);
                    }
                    case string stringInput:
                    {
                        arg = new Address(stringInput);
                        continue;
                    }
                    default:
                    {
                        throw new AbiException(AbiEncodingExceptionMessage);
                    }
                }
            }
        }

        public override Type CSharpType { get; } = typeof(Address);

        public override (object, int) Decode(byte[] data, int position, bool packed)
        {
            return (new Address(data.Slice(position + (packed ? 0 : 12), Address.LengthInBytes)), position + (packed ? Address.LengthInBytes : UInt256.LengthInBytes));
        }
    }
}
