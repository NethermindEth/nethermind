// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Trie
{
    public static class HexPrefix
    {
        public static int ByteLength(byte[] path) => path.Length / 2 + 1;

        public static void CopyToSpan(byte[] path, bool isLeaf, Span<byte> output)
        {
            if (output.Length != ByteLength(path)) throw new ArgumentOutOfRangeException(nameof(output));

            output[0] = (byte)(isLeaf ? 0x20 : 0x000);
            if (path.Length % 2 != 0)
            {
                output[0] += (byte)(0x10 + path[0]);
            }

            for (int i = 0; i < path.Length - 1; i += 2)
            {
                output[i / 2 + 1] =
                    path.Length % 2 == 0
                        ? (byte)(16 * path[i] + path[i + 1])
                        : (byte)(16 * path[i + 1] + path[i + 2]);
            }
        }

        public static byte[] ToBytes(byte[] path, bool isLeaf)
        {
            byte[] output = new byte[path.Length / 2 + 1];

            CopyToSpan(path, isLeaf, output);

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
