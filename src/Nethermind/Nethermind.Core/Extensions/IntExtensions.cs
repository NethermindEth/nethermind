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

using System;
using System.Buffers.Binary;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Core.Extensions
{
    public static class IntExtensions
    {   
        public static ulong GB(this int @this)
        {
            return (ulong)@this * 1_000_000_000UL;
        }
        
        public static ulong MB(this int @this)
        {
            return (ulong)@this * 1_000_000UL;
        }
        
        public static ulong KB(this int @this)
        {
            return (ulong)@this * 1_000UL;
        }
        
        public static ulong GiB(this int @this)
        {
            return (ulong)@this * 1024UL * 1024UL * 1024UL;
        }
        
        public static ulong MiB(this int @this)
        {
            return (ulong)@this * 1024UL * 1024UL;
        }
        
        public static ulong KiB(this int @this)
        {
            return (ulong)@this * 1024UL;
        }
        
        public static UInt256 Ether(this int @this)
        {
            return (uint)@this * Unit.Ether;
        }

        public static UInt256 Wei(this int @this)
        {
            return (uint)@this * Unit.Wei;
        }
        
        public static UInt256 GWei(this int @this)
        {
            return (uint)@this * Unit.GWei;
        }

        public static byte[] ToByteArray(this int value)
        {
            byte[] bytes = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32BigEndian(bytes, value);
            return bytes;
        }
        
        public static byte[] ToBigEndianByteArray(this int value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(bytes);
            }

            return bytes;
        }
    }
}