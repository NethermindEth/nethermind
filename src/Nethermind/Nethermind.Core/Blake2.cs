/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using Nethermind.Core.Extensions;

namespace Nethermind.Core
{
    /// <summary>
    ///     Code adapted from pantheon (https://github.com/PegaSysEng/pantheon)
    /// </summary>
    public class Blake2
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

        private static readonly  ulong[] IV =
        {
            0x6a09e667f3bcc908ul, 0xbb67ae8584caa73bul, 0x3c6ef372fe94f82bul,
            0xa54ff53a5f1d36f1ul, 0x510e527fade682d1ul, 0x9b05688c2b3e6c1ful,
            0x1f83d9abfb41bd6bul, 0x5be0cd19137e2179ul
        };

        private ulong[] _h = new ulong[8];
        private ulong[] _m = new ulong[16];
        private ulong[] _t = new ulong[2];
        private ulong[] _v = new ulong[16];
        private bool _f;
        private uint _rounds = 12;

        public byte[] Compress(byte[] input)
        {
            Init(input);
            
            Array.Copy(_h, 0, _v, 0, 8);
            Array.Copy(IV, 0, _v, 8, 8);

            _v[12] ^= _t[0];
            _v[13] ^= _t[1];

            if (_f)
            {
                _v[14] ^= 0xfffffffffffffffful;
            }

            for (var i = 0; i < _rounds; ++i)
            {
                var s = Precomputed[i % 10];
                Compute(_m[s[0]], _m[s[4]], 0, 4, 8, 12);
                Compute(_m[s[1]], _m[s[5]], 1, 5, 9, 13);
                Compute(_m[s[2]], _m[s[6]], 2, 6, 10, 14);
                Compute(_m[s[3]], _m[s[7]], 3, 7, 11, 15);
                Compute(_m[s[8]], _m[s[12]], 0, 5, 10, 15);
                Compute(_m[s[9]], _m[s[13]], 1, 6, 11, 12);
                Compute(_m[s[10]], _m[s[14]], 2, 7, 8, 13);
                Compute(_m[s[11]], _m[s[15]], 3, 4, 9, 14);
            }

            for (var offset = 0; offset < _h.Length; offset++)
            {
                _h[offset] ^= _v[offset] ^ _v[offset + 8];
            }
            
            var result = new byte[_h.Length * 8];
            for (var i = 0; i < _h.Length; i++)
            {
                Array.Copy(_h[i].ToByteArray(Bytes.Endianness.Little), 0, result, i * 8, 8);
            }

            return result;
        }

        private void Init(byte[] input)
        {
            _rounds = input.Slice(0, 4).ToUInt32();
            var h = input.Slice(4, 64);
            var m = input.Slice(68, 128);
            for (var i = 0; i < _h.Length; i++)
            {
                var offset = i * 8;
                _h[i] = h.Slice(offset, 8).ToUInt64(Bytes.Endianness.Little);
            }

            for (var i = 0; i < _m.Length; i++)
            {
                var offset = i * 8;
                _m[i] = m.Slice(offset, 8).ToUInt64(Bytes.Endianness.Little);
            }

            _t[0] = input.Slice(196, 8).ToUInt64(Bytes.Endianness.Little);
            _t[1] = input.Slice(204, 8).ToUInt64(Bytes.Endianness.Little);
            _f = input[212] != 0;
        }

        private void Compute(ulong a, ulong b, int i, int j, int k, int l)
        {
            _v[i] += a + _v[j];
            _v[l] = RotateLeft(_v[l] ^ _v[i], -32);
            _v[k] += _v[l];
            _v[j] = RotateLeft(_v[j] ^ _v[k], -24);

            _v[i] += b + _v[j];
            _v[l] = RotateLeft(_v[l] ^ _v[i], -16);
            _v[k] += _v[l];
            _v[j] = RotateLeft(_v[j] ^ _v[k], -63);
        }
        
        private static ulong RotateLeft(ulong value, int count)
        {
            return (value << count) | (value >> (64 - count));
        }
    }
}