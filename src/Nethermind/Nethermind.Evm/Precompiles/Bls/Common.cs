using System;
using System.Numerics;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Evm.Precompiles.Bls
{
    public static class Common
    {
        public const int LenFr = 32;
        public const int LenFp = 64;
        
        public static readonly byte[] ZeroResult128 = new byte[128];
        private static readonly byte[] ZeroX16 = new byte[16];

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
                BigInteger w1 = BigInteger.Parse(resultStrings[1]);
                BigInteger w2 = BigInteger.Parse(resultStrings[2]);
            
                result = new byte[2 * LenFp];
                Span<byte> bytes = stackalloc byte[64];
                w1.TryWriteBytes(bytes, out int bytesWritten, true, true);
                bytes.Slice(0, bytesWritten)
                    .CopyTo(result.AsSpan(0 * LenFp + 16 + 48 - bytesWritten, bytesWritten));
            
                w2.TryWriteBytes(bytes, out bytesWritten, true, true);
                bytes.Slice(0, bytesWritten)
                    .CopyTo(result.AsSpan(1 * LenFp + 16 + 48 - bytesWritten, bytesWritten));    
            }
            
            return result;
        }
        
        public static UInt256 ReadScalar(Span<byte> inputDataSpan, int offset)
        {
            var s = inputDataSpan.Slice(offset, 32);
            UInt256.CreateFromBigEndian(out UInt256 scalar, s);
            return scalar;
        }

        public static bool TryReadEthG1(Span<byte> inputDataSpan, int offset, out MclBls12.G1 a)
        {
            bool success;
            if (inputDataSpan.Length < offset + 2 * LenFp)
            {
                a = new MclBls12.G1();
                success = false;
            }
            else
            {
                var x1 = inputDataSpan.Slice(offset + 0 * LenFp, LenFp);
                var y1 = inputDataSpan.Slice(offset + 1 * LenFp, LenFp);
                if (Bytes.AreEqual(ZeroX16, x1.Slice(0, 16)) &&
                    Bytes.AreEqual(ZeroX16, y1.Slice(0, 16)))
                {
                    BigInteger x1Int = new BigInteger(x1.Slice(16), true, true);
                    BigInteger y1Int = new BigInteger(y1.Slice(16), true, true);
                    a = MclBls12.G1.Create(x1Int, y1Int);
                    success = a.IsValid();
                }
                else
                {
                    a = new MclBls12.G1();
                    success = false;
                }   
            }

            return success;
        }
        
        public static void PrepareInputData(byte[] inputData, Span<byte> preparedData)
        {
            inputData ??= Bytes.Empty;
            inputData.AsSpan(0, Math.Min(4 * Common.LenFp, inputData.Length))
                .CopyTo(preparedData.Slice(0, Math.Min(4 * Common.LenFp, inputData.Length)));
        }
    }
}