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
    public class HexPrefix
    {
        [DebuggerStepThrough]
        public HexPrefix(bool isLeaf, params byte[] path)
        {
            IsLeaf = isLeaf;
            Path = path;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static HexPrefix Leaf(params byte[] path)
        {
            return new(true, path);
        }

        public static HexPrefix Leaf(string path)
        {
            return new(true, Bytes.FromHexString(path));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static HexPrefix Extension(params byte[] path)
        {
            return new(false, path);
        }

        public static HexPrefix Extension(string path)
        {
            return new(false, Bytes.FromHexString(path));
        }

        public byte[] Path { get; private set; }
        public bool IsLeaf { get; }
        public bool IsExtension => !IsLeaf;

        public long MemorySize
        {
            get
            {
                long unaligned = MemorySizes.SmallObjectOverhead +
                                MemorySizes.Align(MemorySizes.ArrayOverhead + Path.Length) +
                                1;
                return MemorySizes.Align(unaligned);
            }
        }

        public byte[] ToBytes()
        {
            byte[] output = new byte[Path.Length / 2 + 1];
            output[0] = (byte) (IsLeaf ? 0x20 : 0x000);
            if (Path.Length % 2 != 0)
            {
                output[0] += (byte) (0x10 + Path[0]);
            }

            for (int i = 0; i < Path.Length - 1; i = i + 2)
            {
                output[i / 2 + 1] =
                    Path.Length % 2 == 0
                        ? (byte) (16 * Path[i] + Path[i + 1])
                        : (byte) (16 * Path[i + 1] + Path[i + 2]);
            }

            return output;
        }

        public static HexPrefix FromBytes(ReadOnlySpan<byte> bytes)
        {
            HexPrefix hexPrefix = new(bytes[0] >= 32);
            bool isEven = (bytes[0] & 16) == 0;
            int nibblesCount = bytes.Length * 2 - (isEven ? 2 : 1);
            hexPrefix.Path = new byte[nibblesCount];
            for (int i = 0; i < nibblesCount; i++)
            {
                hexPrefix.Path[i] =
                    isEven
                        ? i % 2 == 0
                            ? (byte) ((bytes[1 + i / 2] & 240) / 16)
                            : (byte) (bytes[1 + i / 2] & 15)
                        : i % 2 == 0
                            ? (byte) (bytes[i / 2] & 15)
                            : (byte) ((bytes[1 + i / 2] & 240) / 16);
            }

            return hexPrefix;
        }

        public override string ToString()
        {
            return ToBytes().ToHexString(false);
        }
    }
}
