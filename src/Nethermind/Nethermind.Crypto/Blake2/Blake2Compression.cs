// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;

namespace Nethermind.Crypto.Blake2
{
    /// <summary>
    ///     Code adapted from pantheon (https://github.com/PegaSysEng/pantheon)
    ///     and from Blake2Fast (https://github.com/saucecontrol/Blake2Fast)
    /// </summary>
    public partial class Blake2Compression
    {
        const byte NumberOfBytesInUlong = 8;
        const byte NumberOfHWords = 8;
        const byte NumberOfMWords = 16;
        const byte StartOfHWords = 4;
        const byte StartOfMWords = 68;
        const byte StartOfTWords = 196;
        const byte ByteOfFWord = 212;

        private static ReadOnlySpan<byte> Ivle => new byte[]
        {
            0x08, 0xC9, 0xBC, 0xF3, 0x67, 0xE6, 0x09, 0x6A, 0x3B, 0xA7, 0xCA, 0x84, 0x85, 0xAE, 0x67, 0xBB, 0x2B,
            0xF8, 0x94, 0xFE, 0x72, 0xF3, 0x6E, 0x3C, 0xF1, 0x36, 0x1D, 0x5F, 0x3A, 0xF5, 0x4F, 0xA5, 0xD1, 0x82,
            0xE6, 0xAD, 0x7F, 0x52, 0x0E, 0x51, 0x1F, 0x6C, 0x3E, 0x2B, 0x8C, 0x68, 0x05, 0x9B, 0x6B, 0xBD, 0x41,
            0xFB, 0xAB, 0xD9, 0x83, 0x1F, 0x79, 0x21, 0x7E, 0x13, 0x19, 0xCD, 0xE0, 0x5B
        };

        private static ReadOnlySpan<byte> Rormask => new byte[]
        {
            3, 4, 5, 6, 7, 0, 1, 2, 11, 12, 13, 14, 15, 8, 9, 10, //r24
            2, 3, 4, 5, 6, 7, 0, 1, 10, 11, 12, 13, 14, 15, 8, 9 //r16
        };

        public unsafe void Compress(ReadOnlySpan<byte> input, Span<byte> output, Blake2CompressMethod method = Blake2CompressMethod.Optimal)
        {
            // sh length = h words length + t[0] + t[1] + f[0]
            ulong* sh = stackalloc ulong[NumberOfHWords + 3];
            ulong* m = stackalloc ulong[NumberOfMWords];

            uint rounds = BinaryPrimitives.ReadUInt32BigEndian(input);

            for (int i = 0; i < NumberOfHWords; i++)
            {
                sh[i] = MemoryMarshal.Cast<byte, ulong>(input.Slice(StartOfHWords + i * NumberOfBytesInUlong, NumberOfBytesInUlong)).GetPinnableReference();
            }

            // t[0]
            sh[8] = MemoryMarshal.Cast<byte, ulong>(input.Slice(StartOfTWords, NumberOfBytesInUlong)).GetPinnableReference();
            // t[1]
            sh[9] = MemoryMarshal.Cast<byte, ulong>(input.Slice(StartOfTWords + NumberOfBytesInUlong, NumberOfBytesInUlong)).GetPinnableReference();
            // f[0]
            sh[10] = input[ByteOfFWord] != 0 ? ulong.MaxValue : ulong.MinValue;

            for (int i = 0; i < NumberOfMWords; i++)
            {
                m[i] = MemoryMarshal.Cast<byte, ulong>(input.Slice(StartOfMWords + i * NumberOfBytesInUlong, NumberOfBytesInUlong)).GetPinnableReference();
            }

            switch (method)
            {
                case Blake2CompressMethod.Optimal when Avx2.IsSupported:
                case Blake2CompressMethod.Avx2:
                    ComputeAvx2(sh, m, rounds);
                    break;
                case Blake2CompressMethod.Optimal when Sse41.IsSupported:
                case Blake2CompressMethod.Sse41:
                    ComputeSse41(sh, m, rounds);
                    break;
                default:
                    ComputeScalar(sh, m, rounds);
                    break;
            }

            Span<ulong> outputUlongs = MemoryMarshal.Cast<byte, ulong>(output);
            for (int offset = 0; offset < NumberOfHWords; offset++)
            {
                outputUlongs[offset] = sh[offset];
            }
        }
    }
}
