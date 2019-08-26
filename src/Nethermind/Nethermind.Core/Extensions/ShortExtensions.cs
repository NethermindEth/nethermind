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
    public static class ShortExtensions
    {
        public static byte[] ToBigEndianByteArray(this short value) => 
            BitConverter.GetBytes(BitConverter.IsLittleEndian ? Swap(value) : value);

        public static byte[] ToBigEndianByteArray(this ushort value) => 
            BitConverter.GetBytes(BitConverter.IsLittleEndian ? Swap(value) : value);


        private static ushort Swap(ushort val)
        {
            unchecked
            {
                return (ushort)(((val & 0xFF00U) >> 8) | ((val & 0x00FFU) << 8));
            }
        }

        private static short Swap(short val)
        {
            unchecked
            {
                return (short)Swap((ushort)val);
            }
        }
    }
}