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
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Extensions;

namespace Nethermind.Trie
{
    public static class HexPrefix
    {
        // public long MemorySize
        // {
        //     get
        //     {
        //         long unaligned = MemorySizes.SmallObjectOverhead +
        //                         MemorySizes.Align(MemorySizes.ArrayOverhead + Path.Length) +
        //                         1;
        //         return MemorySizes.Align(unaligned);
        //     }
        // }

        public static byte[] ToBytes(byte[] path, bool isLeaf)
        {
            byte[] output = new byte[path.Length / 2 + 1];
            output[0] = (byte) (isLeaf ? 0x20 : 0x000);
            if (path.Length % 2 != 0)
            {
                output[0] += (byte) (0x10 + path[0]);
            }

            for (int i = 0; i < path.Length - 1; i = i + 2)
            {
                output[i / 2 + 1] =
                    path.Length % 2 == 0
                        ? (byte) (16 * path[i] + path[i + 1])
                        : (byte) (16 * path[i + 1] + path[i + 2]);
            }

            return output;
        }

        public static (byte[] key, bool isLeaf) FromBytes(ReadOnlySpan<byte> bytes)
        {
            bool isLeaf = bytes[0] >= 32;
            bool isEven = (bytes[0] & 16) == 0;
            int nibblesCount = bytes.Length * 2 - (isEven ? 2 : 1);
            byte[] path = new byte[nibblesCount];
            for (int i = 0; i < nibblesCount; i++)
            {
                path[i] =
                    isEven
                        ? i % 2 == 0
                            ? (byte)((bytes[1 + i / 2] & 240) / 16)
                            : (byte)(bytes[1 + i / 2] & 15)
                        : i % 2 == 0
                            ? (byte)(bytes[i / 2] & 15)
                            : (byte)((bytes[1 + i / 2] & 240) / 16);
            }

            return (path, isLeaf);
        }
    }
}
