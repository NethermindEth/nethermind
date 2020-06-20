using System;
using System.Buffers.Binary;
using Nethermind.Core.Extensions;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Evm.Precompiles.Mcl.Bn256
{
    public class Common
    {
        public const int LenFr = 32;
        public const int LenFp = 32;

        private static readonly byte[] Zero16 = new byte[16];
        private static readonly byte[] ZeroResult64 = new byte[64];

        public static bool ReadFr(in Span<byte> inputDataSpan, in int offset, out Crypto.ZkSnarks.Fr fr)
        {
            fr = new Crypto.ZkSnarks.Fr();
            Span<byte> changed = inputDataSpan.Slice(offset, 32); 
            Bytes.ChangeEndianness8(changed);
            fr.SetLittleEndianMod(changed, 32);
            return true;
        }
        
        public static bool TryReadEthG1(in Span<byte> inputDataSpan, in int offset, out Crypto.ZkSnarks.G1 g1)
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
        
        public static bool TryReadEthG2(in Span<byte> inputDataSpan, in int offset, out Crypto.ZkSnarks.G2 g2)
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

        public static byte[] SerializeEthG1(Crypto.ZkSnarks.G1 g1)
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
                w1.ToBigEndian(result.AsSpan(0, 32));
                w2.ToBigEndian(result.AsSpan(32, 32));
            }

            return result;
        }
    }
}