using Org.BouncyCastle.Crypto.Digests;

namespace Nevermind.Core
{
    public static class Sha3
    {
        public static byte[] Compute(byte[] input)
        {
            var digest = new Sha3Digest();
            byte[] output = new byte[digest.GetDigestSize()];
            digest.BlockUpdate(input, 0, input.Length);
            digest.DoFinal(output, 0);
            return output;
        }

        public static string ComputeString(byte[] input)
        {
            return HexString.FromBytes(Compute(input));
        }

        public static string ComputeString(string input)
        {
            return HexString.FromBytes(Compute(HexString.ToBytes(input)));
        }

    }
}