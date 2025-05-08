// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core;

namespace Nethermind.Trie
{
    /// <summary>
    /// The <see cref="NibblePath"/> uses almost Ethereum encoding for the path.
    /// This ensures that the amount of allocated memory is as small as possible.
    /// </summary>
    /// <remarks>
    /// The 0th byte encodes:
    /// - the oddity of the path
    /// - 0th nibble if the path is odd.
    ///
    /// This allows encoding the prefix of length:
    /// - 1 nibble as 1 byte
    /// - 2 nibbles as 2 bytes
    /// - 3 nibbles as 2 bytes
    /// - 4 nibbles as 3 bytes
    ///
    /// As shown above for prefix of length 1 and 2, it's not worse than byte-per-nibble encoding,
    /// gaining more from 3 nibbles forward.
    /// </remarks>
    public readonly struct NibblePath
    {
        private readonly byte[]? _data;

        private NibblePath(byte[] data)
        {
            _data = data;
        }

        public int MemorySize =>
            _data is not null ? (int)MemorySizes.Align(_data.Length + MemorySizes.ArrayOverhead) : 0;

        /// <summary>
        /// The number of bytes needed to encode the nibble path.
        /// </summary>
        public int ByteLength => _data?.Length ?? 0;

        public bool IsNull => _data is null;

        /// <summary>
        /// The odd flag of the Ethereum encoding, used for oddity of in memory representation as well.
        /// </summary>
        private const byte OddFlag = 0x10;

        /// <summary>
        /// The leaf flag of the Ethereum encoding.
        /// </summary>
        private const byte LeafFlag = 0x20;

        private const byte ZerothMaskForOddPath = 0x0F;

        /// <summary>
        /// A set of single nibble Hex Prefixes.
        /// </summary>
        private static readonly byte[][] Singles =
        [
            [OddFlag | 0],
            [OddFlag | 1],
            [OddFlag | 2],
            [OddFlag | 3],
            [OddFlag | 4],
            [OddFlag | 5],
            [OddFlag | 6],
            [OddFlag | 7],
            [OddFlag | 8],
            [OddFlag | 9],
            [OddFlag | 10],
            [OddFlag | 11],
            [OddFlag | 12],
            [OddFlag | 13],
            [OddFlag | 14],
            [OddFlag | 15]
        ];


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

        public static (NibblePath key, bool isLeaf) FromBytes(ReadOnlySpan<byte> bytes)
        {
            bool isEven = (bytes[0] & OddFlag) == 0;
            bool isLeaf = bytes[0] >= 32;

            if (!isEven && bytes.Length == 1)
            {
                // Special case of single nibble.
                // Use Single to not allocate.
                return (new NibblePath(Singles[bytes[0] & ZerothMaskForOddPath]), isLeaf);
            }

            // Use exactly the same length for the prefix as the 0th byte will be overwritten.
            byte[] path = GC.AllocateUninitializedArray<byte>(bytes.Length);

            // Copy as a whole
            bytes.CopyTo(path);

            // Fix the first byte, so that it has only the 0th odd nibble and oddity flag
            path[0] = (byte)(path[0] & (ZerothMaskForOddPath | OddFlag));

            return (new NibblePath(path), isLeaf);
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

        public void EncodeTo(Span<byte> destination, bool isLeaf)
        {
            Debug.Assert(_data != null);
            _data.CopyTo(destination);
            destination[0] = (byte)(isLeaf ? LeafFlag : 0);
        }
    }
}
