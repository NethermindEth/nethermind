// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.Trie
{
    public static class HexPrefix
    {
        public static int ByteLength(in TreePath path) => path.Length / 2 + 1;

        public static void CopyToSpan(in TreePath path, bool isLeaf, Span<byte> output)
        {
            if (output.Length != ByteLength(path)) throw new ArgumentOutOfRangeException(nameof(output));

            output[0] = (byte)(isLeaf ? 0x20 : 0x000);
            if (path.Length % 2 != 0)
            {
                output[0] += (byte)(0x10 + path[0]);
                path.Span[..((path.Length) / 2)].CopyTo(output[1..]);
                Bytes.ShiftLeft4(output[1..]);
                output[path.Length / 2] |= path[^1];
            }
            else
            {
                path.Span[..((path.Length + 1) / 2)].CopyTo(output[1..]);
            }
        }

        public static byte[] ToBytes(in TreePath path, bool isLeaf)
        {
            byte[] output = new byte[path.Length / 2 + 1];

            CopyToSpan(path, isLeaf, output);

            return output;
        }

        public static byte[] ToBytes(BoxedTreePath path, bool isLeaf)
        {
            return ToBytes(path.TreePath, isLeaf);
        }


        public static (TreePath key, bool isLeaf) FromBytes(ReadOnlySpan<byte> bytes)
        {
            bool isLeaf = bytes[0] >= 32;
            bool isEven = (bytes[0] & 16) == 0;
            int nibblesCount = bytes.Length * 2 - (isEven ? 2 : 1);
            TreePath path = new TreePath(Keccak.Zero, nibblesCount);

            if (isEven)
            {
                bytes[1..((nibblesCount / 2) + 1)].CopyTo(path.Span);
                path.Length = nibblesCount;
            }
            else
            {
                bytes[..(nibblesCount / 2)].CopyTo(path.Span); // Note: last byte not copied as there might not be enough space.
                Bytes.ShiftLeft4(path.Span[..(nibblesCount / 2)]);
                if (nibblesCount > 1)
                {
                    path[^2] = (byte)(bytes[(nibblesCount / 2)] >> 4);
                }
                path[^1] = (byte)((bytes[(nibblesCount / 2)]) & 0x0f);
                path.Length = nibblesCount;
            }

            return (path, isLeaf);
        }
    }
}
