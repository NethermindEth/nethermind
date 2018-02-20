/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;

namespace Nethermind.Core.Extensions
{
    public static class Int64Extensions
    {
        public static byte[] ToBigEndianByteArray(this long value)
        {
            return BitConverter.GetBytes(BitConverter.IsLittleEndian ? Swap(value) : value);
        }

        // https://antonymale.co.uk/converting-endianness-in-csharp.html
        private static ulong Swap(ulong val)
        {
            // Swap adjacent 32-bit blocks
            val = (val >> 32) | (val << 32);
            // Swap adjacent 16-bit blocks
            val = ((val & 0xFFFF0000FFFF0000U) >> 16) | ((val & 0x0000FFFF0000FFFFU) << 16);
            // Swap adjacent 8-bit blocks
            val = ((val & 0xFF00FF00FF00FF00U) >> 8) | ((val & 0x00FF00FF00FF00FFU) << 8);
            return val;
        }

        // https://antonymale.co.uk/converting-endianness-in-csharp.html
        private static long Swap(long val)
        {
            unchecked
            {
                return (long)Swap((ulong)val);
            }
        }
    }
}