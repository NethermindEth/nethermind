//  Copyright (c) 2018 Demerzel Solutions Limited
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

using System.Buffers.Binary;
using Nethermind.Core.Extensions;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Core.Json
{
    public static class Extensions
    {
        public static string ToHexString(this long value, bool skipLeadingZeros)
        {
            if (value == UInt256.Zero)
            {
                return "0x";
            }

            byte[] bytes = new byte[8];
            BinaryPrimitives.WriteInt64BigEndian(bytes, value);
            return bytes.ToHexString(true, skipLeadingZeros, false);
        }
        
        public static string ToHexString(this UInt256 value, bool skipLeadingZeros)
        {
            if (value == UInt256.Zero)
            {
                return "0x";
            }

            if (value == UInt256.One)
            {
                return "0x1";
            }
            
            byte[] bytes = new byte[32];
            value.ToBigEndian(bytes);
            return bytes.ToHexString(true, skipLeadingZeros, false);
        }
    }
}