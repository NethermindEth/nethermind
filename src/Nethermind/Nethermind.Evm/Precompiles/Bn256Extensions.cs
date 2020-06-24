//  Copyright (c) 2020s Demerzel Solutions Limited
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
using Nethermind.Core.Extensions;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Evm.Precompiles
{
    public static class Bn256Extensions
    {
        public const int LenFr = 32;
        public const int LenFp = 32;
        
        private static readonly byte[] ZeroResult64 = new byte[64];
        
        public static bool ReadFr(this Span<byte> inputDataSpan, in int offset, out Crypto.ZkSnarks.Fr fr)
        {
            fr = new Crypto.ZkSnarks.Fr();
            Span<byte> changed = inputDataSpan.Slice(offset, LenFr);
            Bytes.ChangeEndianness8(changed);
            fr.SetLittleEndianMod(changed, LenFr);
            return true;
        }

        public static bool TryReadEthG1(this Span<byte> inputDataSpan, in int offset, out Crypto.ZkSnarks.G1 g1)
        {
            bool success;
            if (inputDataSpan.Length < offset + 2 * LenFp)
            {
                g1 = new Crypto.ZkSnarks.G1();
                success = false;
            }
            else
            {
                UInt256.CreateFromBigEndian(out UInt256 x1Int, inputDataSpan.Slice(offset + 0 * LenFp, LenFp));
                UInt256.CreateFromBigEndian(out UInt256 y1Int, inputDataSpan.Slice(offset + 1 * LenFp, LenFp));
                g1 = Crypto.ZkSnarks.G1.Create(x1Int, y1Int);
                success = g1.IsValid();
            }

            return success;
        }

        public static bool TryReadEthG2(this Span<byte> inputDataSpan, in int offset, out Crypto.ZkSnarks.G2 g2)
        {
            bool success;
            if (inputDataSpan.Length < offset + 4 * LenFp)
            {
                g2 = new Crypto.ZkSnarks.G2();
                success = false;
            }
            else
            {
                UInt256.CreateFromBigEndian(out UInt256 bInt, inputDataSpan.Slice(offset + 0 * LenFp, LenFp));
                UInt256.CreateFromBigEndian(out UInt256 aInt, inputDataSpan.Slice(offset + 1 * LenFp, LenFp));
                UInt256.CreateFromBigEndian(out UInt256 dInt, inputDataSpan.Slice(offset + 2 * LenFp, LenFp));
                UInt256.CreateFromBigEndian(out UInt256 cInt, inputDataSpan.Slice(offset + 3 * LenFp, LenFp));
                g2 = Crypto.ZkSnarks.G2.Create(aInt, bInt, cInt, dInt);
                success = g2.IsValid();
            }

            return success;
        }

        public static byte[] SerializeEthG1(this Crypto.ZkSnarks.G1 g1)
        {
            byte[] result;
            if (g1.IsZero())
            {
                result = ZeroResult64;
            }
            else
            {
                string[] resultStrings = g1.GetStr(0).Split(" ");
                UInt256 w1 = UInt256.Parse(resultStrings[1]);
                UInt256 w2 = UInt256.Parse(resultStrings[2]);
                result = new byte[64];
                w1.ToBigEndian(result.AsSpan(0, LenFr));
                w2.ToBigEndian(result.AsSpan(LenFr, LenFr));
            }

            return result;
        }

        public static UInt256 ReadMclScalar(this Span<byte> inputDataSpan, in int offset)
        {
            Span<byte> s = inputDataSpan.Slice(offset, LenFr);
            UInt256.CreateFromBigEndian(out UInt256 scalar, s);
            return scalar;
        }
    }
}