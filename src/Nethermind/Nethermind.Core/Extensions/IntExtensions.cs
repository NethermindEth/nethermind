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
using System.Buffers.Binary;
using Nethermind.Int256;

namespace Nethermind.Core.Extensions
{
    public static class SizeExtensions
    {
        public static long GB(this int @this)
        {
            return @this * 1_000_000_000L;
        }
        
        public static long MB(this int @this)
        {
            return @this * 1_000_000L;
        }
        
        public static long KB(this int @this)
        {
            return @this * 1_000L;
        }
        
        public static long GiB(this int @this)
        {
            return @this * 1024L * 1024L * 1024L;
        }
        
        public static long MiB(this int @this)
        {
            return @this * 1024L * 1024L;
        }
        
        public static long KiB(this int @this)
        {
            return @this * 1024L;
        }
        
        public static long GB(this long @this)
        {
            return ((int)@this).GB();
        }
        
        public static long MB(this long @this)
        {
            return ((int)@this).MB();
        }
        
        public static long KB(this long @this)
        {
            return ((int)@this).KB();
        }
        
        public static long GiB(this long @this)
        {
            return ((int)@this).GiB();
        }
        
        public static long MiB(this long @this)
        {
            return ((int)@this).MiB();
        }
        
        public static long KiB(this long @this)
        {
            return ((int)@this).KiB();
        }
    }
    
    public static class IntExtensions
    {
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
