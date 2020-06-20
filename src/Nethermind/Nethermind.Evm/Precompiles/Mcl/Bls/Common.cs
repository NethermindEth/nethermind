using System;
using System.Numerics;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Evm.Precompiles.Mcl.Bls
{
    public static class Common
    {
        public const int LenFr = 32;
        public const int LenFp = 64;

        private static readonly byte[] Zero16 = new byte[16];
        private static readonly byte[] ZeroResult128 = new byte[128];
        private static readonly byte[] ZeroResult256 = new byte[256];

        public static byte[] SerializeEthG1(MclBls12.G1 g1)
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
        
        public static byte[] SerializeEthG2(MclBls12.G2 g2)
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
        
        public static bool TryReadEthG1(in Span<byte> inputDataSpan, in int offset, out MclBls12.G1 g1)
        {
            bool success;
            if (inputDataSpan.Length < offset + 2 * LenFp)
            {
                g1 = new MclBls12.G1();
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
                    g1 = MclBls12.G1.Create(x1Int, y1Int);
                    success = g1.IsValid();
                }
                else
                {
                    g1 = new MclBls12.G1();
                    success = false;
                }   
            }

            return success;
        }

        public static bool TryReadEthG2(in Span<byte> inputDataSpan, in int offset, out MclBls12.G2 g2)
        {
            bool success;
            if (inputDataSpan.Length < offset + 4 * LenFp)
            {
                g2 = new MclBls12.G2();
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
                    g2 = MclBls12.G2.Create(aInt, bInt, cInt, dInt);
                    success = g2.IsValid();
                }
                else
                {
                    g2 = new MclBls12.G2();
                    success = false;
                }   
            }

            return success;
        }
    }
}