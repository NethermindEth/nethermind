using System.Text;
using Org.BouncyCastle.Crypto.Digests;

namespace Nevermind.Core
{
    public static class Keccak
    {
        public static byte[] Compute(byte[] input)
        {
            KeccakDigest digest = new KeccakDigest(256);
            byte[] output = new byte[digest.GetDigestSize()];
            digest.BlockUpdate(input, 0, input.Length);
            digest.DoFinal(output, 0);
            return output;
        }

        public static string ComputeString(byte[] input)
        {
            return HexString.FromBytes(Compute(input));
        }

        public static byte[] Compute(string input)
        {
            return Compute(Encoding.UTF8.GetBytes(input));
        }

        public static string ComputeString(string input)
        {
            return HexString.FromBytes(Compute(HexString.ToBytes(input)));
        }
    }
}