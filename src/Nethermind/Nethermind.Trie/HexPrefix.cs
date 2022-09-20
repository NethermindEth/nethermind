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
        // flags for the first byte
        private const int LeafFlag = 0x20;
        private const int ExtensionFlag = 0x000;
        private const int OddFlag = 0x10;
        private const int OddShift = 4;

        // nibbles
        private const int LowNibbleMask = 0x0F;
        private const int HighNibbleMask = 0xF0;
        private const int NibbleSize = 16;
        private const int NibbleShift = 4;

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
            output[0] = (byte)(IsLeaf ? LeafFlag : ExtensionFlag);
            if (Path.Length % 2 != 0)
            {
                output[0] += (byte)(OddFlag + Path[0]);
            }

            for (int i = 0; i < Path.Length - 1; i = i + 2)
            {
                output[i / 2 + 1] =
                    Path.Length % 2 == 0
                        ? (byte)(NibbleSize * Path[i] + Path[i + 1])
                        : (byte)(NibbleSize * Path[i + 1] + Path[i + 2]);
            }

            return output;
        }

        public static HexPrefix FromBytes(ReadOnlySpan<byte> bytes)
        {
            HexPrefix hexPrefix = new(bytes[0] >= LeafFlag);
            bool isEven = (bytes[0] & OddFlag) == 0;
            int nibblesCount = bytes.Length * 2 - (isEven ? 2 : 1);
            hexPrefix.Path = new byte[nibblesCount];
            for (int i = 0; i < nibblesCount; i++)
            {
                hexPrefix.Path[i] =
                    isEven
                        ? i % 2 == 0
                            ? (byte)((bytes[1 + i / 2] & HighNibbleMask) / NibbleSize)
                            : (byte)(bytes[1 + i / 2] & LowNibbleMask)
                        : i % 2 == 0
                            ? (byte)(bytes[i / 2] & LowNibbleMask)
                            : (byte)((bytes[1 + i / 2] & HighNibbleMask) / NibbleSize);
            }

            return hexPrefix;
        }

        public override string ToString()
        {
            return ToBytes().ToHexString(false);
        }

        public static byte[] CombinePaths(byte[] nodePath, byte[] nextNodePath)
        {
            return Bytes.Concat(nodePath, nextNodePath);
        }

        public static byte[] CombinePaths(byte child, byte[] nextNodePath)
        {
            return Bytes.Concat(child, nextNodePath);
        }

        public static bool PathsAreEqual(Span<byte> a, Span<byte> b)
        {
            return Bytes.AreEqual(a, b);
        }

        public static class RawPath
        {
            public static byte [] Combine(byte child, byte[] path)
            {
                bool isEven = (path[0] & OddFlag) == 0;

                if (isEven)
                {
                    // there's one nibble in front empty, allocate the same
                    byte[] result = new byte[path.Length];

                    // copy whole
                    Buffer.BlockCopy(path, 0, result, 0, path.Length);

                    // overwrite first only with odd flag
                    result[0] = (byte)(OddFlag | child);
                    return result;
                }
                else
                {
                    // no empty nibble in 0th byte, allocate one bigger
                    byte[] result = new byte[path.Length + 1];

                    // copy all but 0th nibble to the new one, leaving 2 bytes first empty
                    Buffer.BlockCopy(path, 1, result, 2, path.Length - 1);

                    // the result is even, no need to write the first nibble, empty by allocs
                    // result[0] = (byte)(0);

                    // write two nibbles, the new one first then the previous one
                    result[1] = (byte)((child << NibbleShift) | (path[0] & LowNibbleMask));
                    return result;
                }
            }
        }
    }
}
