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

namespace Nethermind.Core.Extensions
{
    public static class Int64Extensions
    {
        public static byte[] ToBigEndianByteArray(this long value)
        {
            byte[] bytes = new byte[8];
            bytes[7] = (byte)value;
            bytes[6] = (byte)(value >> 8);
            bytes[5] = (byte)(value >> 16);
            bytes[4] = (byte)(value >> 24);
            bytes[3] = (byte)(value >> 32);
            bytes[2] = (byte)(value >> 40);
            bytes[1] = (byte)(value >> 48);
            bytes[0] = (byte)(value >> 56);
            return bytes;
        }
    }
}