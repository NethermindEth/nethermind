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
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nethermind.Crypto
{
    /// <summary>
    ///     Code adapted from pantheon (https://github.com/PegaSysEng/pantheon)
    /// </summary>
    public class Blake2Compression
    {
        private static readonly byte[][] Precomputed =
        {
            new byte[] {0, 2, 4, 6, 1, 3, 5, 7, 8, 10, 12, 14, 9, 11, 13, 15},
            new byte[] {14, 4, 9, 13, 10, 8, 15, 6, 1, 0, 11, 5, 12, 2, 7, 3},
            new byte[] {11, 12, 5, 15, 8, 0, 2, 13, 10, 3, 7, 9, 14, 6, 1, 4},
            new byte[] {7, 3, 13, 11, 9, 1, 12, 14, 2, 5, 4, 15, 6, 10, 0, 8},
            new byte[] {9, 5, 2, 10, 0, 7, 4, 15, 14, 11, 6, 3, 1, 12, 8, 13},
            new byte[] {2, 6, 0, 8, 12, 10, 11, 3, 4, 7, 15, 1, 13, 5, 14, 9},
            new byte[] {12, 1, 14, 4, 5, 15, 13, 10, 0, 6, 9, 8, 7, 3, 2, 11},
            new byte[] {13, 7, 12, 3, 11, 14, 1, 9, 5, 15, 8, 2, 0, 4, 6, 10},
            new byte[] {6, 14, 11, 0, 15, 9, 3, 8, 12, 13, 1, 10, 2, 7, 4, 5},
            new byte[] {10, 8, 7, 1, 2, 4, 6, 5, 15, 9, 3, 13, 11, 14, 12, 0}
        };

        private static readonly ulong[] IV =
        {
            0x6a09e667f3bcc908ul, 0xbb67ae8584caa73bul, 0x3c6ef372fe94f82bul,
            0xa54ff53a5f1d36f1ul, 0x510e527fade682d1ul, 0x9b05688c2b3e6c1ful,
            0x1f83d9abfb41bd6bul, 0x5be0cd19137e2179ul
        };
        
        public void Compress(ReadOnlySpan<byte> input, Span<byte> output)
        {
            Span<ulong> v = stackalloc ulong[16];

            uint rounds = BinaryPrimitives.ReadUInt32BigEndian(input);
            ReadOnlySpan<ulong> h = MemoryMarshal.Cast<byte, ulong>(input.Slice(4, 64));
            ReadOnlySpan<ulong> m = MemoryMarshal.Cast<byte, ulong>(input.Slice(68, 128));
            ReadOnlySpan<ulong> t = MemoryMarshal.Cast<byte, ulong>(input.Slice(196, 16));
            bool f = input[212] != 0;
            
            h.CopyTo(v.Slice(0, 8));
            IV.AsSpan().CopyTo(v.Slice(8, 8));

            v[12] ^= t[0];
            v[13] ^= t[1];

            if (f)
            {
                v[14] ^= 0xfffffffffffffffful;
            }

            for (uint i = 0; i < rounds; ++i)
            {
                byte[] s = Precomputed[i % 10];
                Compute(v, m[s[0]], m[s[4]], 0, 4, 8, 12);
                Compute(v, m[s[1]], m[s[5]], 1, 5, 9, 13);
                Compute(v, m[s[2]], m[s[6]], 2, 6, 10, 14);
                Compute(v, m[s[3]], m[s[7]], 3, 7, 11, 15);
                Compute(v, m[s[8]], m[s[12]], 0, 5, 10, 15);
                Compute(v, m[s[9]], m[s[13]], 1, 6, 11, 12);
                Compute(v, m[s[10]], m[s[14]], 2, 7, 8, 13);
                Compute(v, m[s[11]], m[s[15]], 3, 4, 9, 14);
            }

            MemoryMarshal.Cast<ulong, byte>(h).CopyTo(output);
            Span<ulong> outputUlongs = MemoryMarshal.Cast<byte, ulong>(output);
            for (int offset = 0; offset < h.Length; offset++)
            {
                outputUlongs[offset] = h[offset] ^ v[offset] ^ v[offset + 8];
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void Compute(Span<ulong> v, ulong a, ulong b, int i, int j, int k, int l)
        {
            v[i] += a + v[j];
            v[l] = RotateLeft(v[l] ^ v[i], -32);
            v[k] += v[l];
            v[j] = RotateLeft(v[j] ^ v[k], -24);

            v[i] += b + v[j];
            v[l] = RotateLeft(v[l] ^ v[i], -16);
            v[k] += v[l];
            v[j] = RotateLeft(v[j] ^ v[k], -63);
        }
        
        private static ulong RotateLeft(ulong value, int count)
        {
            return (value << count) | (value >> (64 - count));
        }
    }
}
