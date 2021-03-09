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

namespace Nethermind.Abi
{
    public class AbiString : AbiType
    {
        public static AbiString Instance = new();

        private AbiString()
        {
        }

        public override bool IsDynamic => true;

        public override string Name => "string";

        public override (object, int) Decode(byte[] data, int position, bool packed)
        {
            (object bytes, int newPosition) = DynamicBytes.Decode(data, position, packed);
            return (Encoding.ASCII.GetString((byte[]) bytes), newPosition);
        }

        public override byte[] Encode(object? arg, bool packed)
        {
            if (arg is string input)
            {
                return DynamicBytes.Encode(Encoding.ASCII.GetBytes(input), packed);
            }

            throw new AbiException(AbiEncodingExceptionMessage);
        }

        public override Type CSharpType { get; } = typeof(string);
    }
}
