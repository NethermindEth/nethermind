// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nethermind.Trie
{
    public static class HexPrefix
    {
        public static int ByteLength(byte[] path) => path.Length / 2 + 1;

        public static void CopyToSpan(byte[] path, bool isLeaf, Span<byte> output)
        {
            if (output.Length != ByteLength(path)) throw new ArgumentOutOfRangeException(nameof(output));

            output[0] = (byte)(isLeaf ? 0x20 : 0x00);
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
            bool isEven = (bytes[0] & 16) == 0;
            int nibblesCount = bytes.Length * 2 - (isEven ? 2 : 1);
            byte[] path = new byte[nibblesCount];
            Span<byte> span = new(path);
            if (!isEven)
            {
                span[0] = (byte)(bytes[0] & 0xF);
                span = span[1..];
            }
            bool isLeaf = bytes[0] >= 32;
            bytes = bytes[1..];

            Span<ushort> nibbles = MemoryMarshal.CreateSpan(
                ref Unsafe.As<byte, ushort>(ref MemoryMarshal.GetReference(span)),
                span.Length / 2);

            Debug.Assert(nibbles.Length == bytes.Length);

            ref byte byteRef = ref MemoryMarshal.GetReference(bytes);
            ref ushort lookup16 = ref MemoryMarshal.GetArrayDataReference(Lookup16);
            for (int i = 0; i < nibbles.Length; i++)
            {
                nibbles[i] = Unsafe.Add(ref lookup16, Unsafe.Add(ref byteRef, i));
            }

            return (path, isLeaf);
        }

        private static readonly ushort[] Lookup16 = CreateLookup16("x2");

        private static ushort[] CreateLookup16(string format)
        {
            ushort[] result = new ushort[256];
            for (int i = 0; i < 256; i++)
            {
                result[i] = (ushort)(((i & 0xF) << 8) | ((i & 240) >> 4));
            }

            return result;
        }
    }
}
