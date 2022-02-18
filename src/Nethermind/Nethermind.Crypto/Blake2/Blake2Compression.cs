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
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;

namespace Nethermind.Crypto.Blake2
{
    /// <summary>
    ///     Code adapted from pantheon (https://github.com/PegaSysEng/pantheon)
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

        public unsafe void Compress(ReadOnlySpan<byte> input, Span<byte> output)
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

            if (Avx2.IsSupported)
            {
                ComputeAvx2(sh, m, rounds);
            }
			else if (Sse41.IsSupported)
            {
                // mixSse41(sh, m);
            }
            else
            {
                // ComputeScalar(sh, m, rounds);
            }
            
            Span<ulong> outputUlongs = MemoryMarshal.Cast<byte, ulong>(output);
            for (int offset = 0; offset < NumberOfHWords; offset++)
            {
                outputUlongs[offset] = sh[offset];
            }
        }
    }
}
