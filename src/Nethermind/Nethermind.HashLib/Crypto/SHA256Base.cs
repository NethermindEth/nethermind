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
using Nethermind.HashLib.Extensions;

namespace Nethermind.HashLib.Crypto
{
    internal abstract class SHA256Base : BlockHash, ICryptoNotBuildIn
    {
        protected readonly uint[] m_state = new uint[8];

        #region Consts

        private static readonly uint[] s_K = 
        {
            0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
            0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
            0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
            0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
            0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85, 
            0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
            0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
            0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2
        };

        #endregion

        public SHA256Base(int a_hash_size)
            : base(a_hash_size, 64)
        {
            Initialize();
        }

        protected override void Finish()
        {
            ulong bits = m_processed_bytes * 8;
            int padindex = (m_buffer.Pos < 56) ? (56 - m_buffer.Pos) : (120 - m_buffer.Pos);

            Span<byte> pad = stackalloc byte[padindex + 8];
            pad[0] = 0x80;

            Converters.ConvertULongToBytesSwapOrder(bits, pad, padindex);
            padindex += 8;

            TransformBytes(pad, 0, padindex);
        }

        protected override void TransformBlock(ReadOnlySpan<byte> a_data, int a_index)
        {
            Span<uint> data = stackalloc uint[64];
            Converters.ConvertBytesToUIntsSwapOrder(a_data, a_index, BlockSize, data, 0);

            uint A = m_state[0];
            uint B = m_state[1];
            uint C = m_state[2];
            uint D = m_state[3];
            uint E = m_state[4];
            uint F = m_state[5];
            uint G = m_state[6];
            uint H = m_state[7];

            for (int r = 16; r < 64; r++)
            {
                uint T = data[r - 2];
                uint T2 = data[r - 15];
                data[r] = (((T >> 17) | (T << 15)) ^ ((T >> 19) | (T << 13)) ^ (T >> 10)) + data[r - 7] +
                          (((T2 >> 7) | (T2 << 25)) ^ ((T2 >> 18) | (T2 << 14)) ^ (T2 >> 3)) + data[r - 16];
            }

            for (int r = 0; r < 64; r++)
            {
                uint T = s_K[r] + data[r] + H + (((E >> 6) | (E << 26)) ^ ((E >> 11) | (E << 21)) ^ ((E >> 25) |
                                                                                                     (E << 7))) + ((E & F) ^ (~E & G));
                uint T2 = (((A >> 2) | (A << 30)) ^ ((A >> 13) | (A << 19)) ^
                           ((A >> 22) | (A << 10))) + ((A & B) ^ (A & C) ^ (B & C));
                H = G;
                G = F;
                F = E;
                E = D + T;
                D = C;
                C = B;
                B = A;
                A = T + T2;
            }

            m_state[0] += A;
            m_state[1] += B;
            m_state[2] += C;
            m_state[3] += D;
            m_state[4] += E;
            m_state[5] += F;
            m_state[6] += G;
            m_state[7] += H;
        }
        
        protected override void TransformBlock(byte[] a_data, int a_index)
        {
            Span<uint> data = stackalloc uint[64];
            Converters.ConvertBytesToUIntsSwapOrder(a_data, a_index, BlockSize, data, 0);

            uint A = m_state[0];
            uint B = m_state[1];
            uint C = m_state[2];
            uint D = m_state[3];
            uint E = m_state[4];
            uint F = m_state[5];
            uint G = m_state[6];
            uint H = m_state[7];

            for (int r = 16; r < 64; r++)
            {
                uint T = data[r - 2];
                uint T2 = data[r - 15];
                data[r] = (((T >> 17) | (T << 15)) ^ ((T >> 19) | (T << 13)) ^ (T >> 10)) + data[r - 7] +
                    (((T2 >> 7) | (T2 << 25)) ^ ((T2 >> 18) | (T2 << 14)) ^ (T2 >> 3)) + data[r - 16];
            }

            for (int r = 0; r < 64; r++)
            {
                uint T = s_K[r] + data[r] + H + (((E >> 6) | (E << 26)) ^ ((E >> 11) | (E << 21)) ^ ((E >> 25) |
                         (E << 7))) + ((E & F) ^ (~E & G));
                uint T2 = (((A >> 2) | (A << 30)) ^ ((A >> 13) | (A << 19)) ^
                          ((A >> 22) | (A << 10))) + ((A & B) ^ (A & C) ^ (B & C));
                H = G;
                G = F;
                F = E;
                E = D + T;
                D = C;
                C = B;
                B = A;
                A = T + T2;
            }

            m_state[0] += A;
            m_state[1] += B;
            m_state[2] += C;
            m_state[3] += D;
            m_state[4] += E;
            m_state[5] += F;
            m_state[6] += G;
            m_state[7] += H;
        }
    }
}
