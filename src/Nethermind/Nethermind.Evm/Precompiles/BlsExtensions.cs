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
using System.Numerics;
using Nethermind.Core.Extensions;
using Nethermind.Crypto.Bls;

namespace Nethermind.Evm.Precompiles
{
    public static class BlsExtensions
    {
        public const int LenFr = 32;
        public const int LenFp = 64;
        
        private static readonly byte[] Zero16 = new byte[16];
        
        private static readonly byte[] ZeroResult128 = new byte[128];
        private static readonly byte[] ZeroResult256 = new byte[256];

        public static byte[] SerializeEthG1(this G1 g1)
        {
            byte[] result;
            if (g1.IsZero())
            {
                result = ZeroResult128;
            }
            else
            {
                string[] resultStrings = g1.GetStr(0).Split(" ");
                result = SerializeEth(resultStrings);   
            }
            
            return result;
        }
        
        public static byte[] SerializeEthG2(G2 g2)
        {
            byte[] result;
            if (g2.IsZero())
            {
                result = ZeroResult256;
            }
            else
            {
                // TODO: can remove this split nd instead keep finding index of <- check in the benchmark
                string[] resultStrings = g2.GetStr(0).Split(" ");
                result = SerializeEth(resultStrings);
            }
            
            return result;
        }

        private static byte[] SerializeEth(string[] resultStrings)
        {
            // the first item describes the format - 1
            int fpCount = resultStrings.Length - 1;
            byte[] result = new byte[fpCount * LenFp];
            Span<byte> bytes = stackalloc byte[LenFp];
            for (int i = 0; i < fpCount; i++)
            {
                BigInteger w = BigInteger.Parse(resultStrings[i + 1]);
                w.TryWriteBytes(bytes, out int bytesWritten, true, true);
                bytes.Slice(0, bytesWritten)
                    .CopyTo(result.AsSpan(i * LenFp + 16 + 48 - bytesWritten, bytesWritten));
            }

            return result;
        }

        public static bool TryReadFp(this Span<byte> inputDataSpan, in int offset, out Fp fp)
        {
            bool success;
            if (inputDataSpan.Length < offset + LenFp ||
                !Bytes.AreEqual(Zero16, inputDataSpan.Slice(offset, 16)))
            {
                fp = new Fp();
                success = false;
            }
            else
            {
                Span<byte> fpBytes = inputDataSpan.Slice(offset + 0 * LenFp, LenFp);
                // BigInteger fpInt = new BigInteger(fpBytes.Slice(16), true, true);
                fp = new Fp();

                // fpInt = fpInt % MclBls12.P;
                
                Bytes.ChangeEndianness8(fpBytes);
                fp.FpSetLittleEndianMod(fpBytes, 48);
                // fp.SetStr(fpInt.ToString(), 10);
                success = fp.IsValid();
            }

            return success;
        }
        
        public static bool TryReadFp2(this Span<byte> inputDataSpan, in int offset, out Fp2 fp)
        {
            bool success = TryReadFp(inputDataSpan, offset, out Fp fp0);
            success &= TryReadFp(inputDataSpan, offset + LenFp, out Fp fp1);
            if (success)
            {
                fp = new Fp2(fp0, fp1);
            }
            else
            {
                fp = new Fp2();
            }
            
            return success;
        }
        
        public static bool TryReadEthFr(this Span<byte> inputDataSpan, in int offset, out Fr fr)
        {
            bool success;
            if (inputDataSpan.Length < offset + LenFr)
            {
                fr = new Fr();
                success = false;
            }
            else
            {
                Span<byte> frBytes = inputDataSpan.Slice(offset, LenFr);
                fr = new Fr();
                
                Bytes.ChangeEndianness8(frBytes);
                fr.SetLittleEndianMod(frBytes, 32);
                success = fr.IsValid();
            }

            return success;
        }
        
        public static bool TryReadEthG1(this Span<byte> inputDataSpan, in int offset, out G1 g1)
        {
            bool success;
            if (inputDataSpan.Length < offset + 2 * LenFp)
            {
                g1 = new G1();
                success = false;
            }
            else
            {
                Span<byte> x1 = inputDataSpan.Slice(offset + 0 * LenFp, LenFp);
                Span<byte> y1 = inputDataSpan.Slice(offset + 1 * LenFp, LenFp);
                if (Bytes.AreEqual(Zero16, x1.Slice(0, 16)) &&
                    Bytes.AreEqual(Zero16, y1.Slice(0, 16)))
                {
                    BigInteger x1Int = new BigInteger(x1.Slice(16), true, true);
                    BigInteger y1Int = new BigInteger(y1.Slice(16), true, true);
                    g1 = G1.Create(x1Int, y1Int);
                    success = g1.IsValid();
                }
                else
                {
                    g1 = new G1();
                    success = false;
                }   
            }

            return success;
        }

        public static bool TryReadEthG2(this Span<byte> inputDataSpan, in int offset, out G2 g2)
        {
            bool success;
            if (inputDataSpan.Length < offset + 4 * LenFp)
            {
                g2 = new G2();
                success = false;
            }
            else
            {
                Span<byte> a = inputDataSpan.Slice(offset + 0 * LenFp, LenFp);
                Span<byte> b = inputDataSpan.Slice(offset + 1 * LenFp, LenFp);
                Span<byte> c = inputDataSpan.Slice(offset + 2 * LenFp, LenFp);
                Span<byte> d = inputDataSpan.Slice(offset + 3 * LenFp, LenFp);
                if (Bytes.AreEqual(Zero16, a.Slice(0, 16)) &&
                    Bytes.AreEqual(Zero16, b.Slice(0, 16)) &&
                    Bytes.AreEqual(Zero16, c.Slice(0, 16)) &&
                    Bytes.AreEqual(Zero16, d.Slice(0, 16)))
                {
                    BigInteger aInt = new BigInteger(a.Slice(16), true, true);
                    BigInteger bInt = new BigInteger(b.Slice(16), true, true);
                    BigInteger cInt = new BigInteger(c.Slice(16), true, true);
                    BigInteger dInt = new BigInteger(d.Slice(16), true, true);
                    g2 = G2.Create(aInt, bInt, cInt, dInt);
                    success = g2.IsValid();
                }
                else
                {
                    g2 = new G2();
                    success = false;
                }   
            }

            return success;
        }
    }
}