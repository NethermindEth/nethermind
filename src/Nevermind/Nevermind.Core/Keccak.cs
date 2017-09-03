using System.Text;
using HashLib;

namespace Nevermind.Core
{
    public static class Keccak
    {
        public static byte[] Compute(byte[] input)
        {
            IHash keccak256 = HashFactory.Crypto.SHA3.CreateKeccak256();
            string value =  keccak256.ComputeBytes(input).ToString();
            return HexString.ToBytes(value.ToLowerInvariant().Replace("-", string.Empty));
            //KeccakDigest digest = new KeccakDigest(256);
            //byte[] output = new byte[digest.GetDigestSize()];
            //digest.BlockUpdate(input, 0, input.Length);
            //digest.DoFinal(output, 0);
            //return output;
        }

        public static string ComputeString(byte[] input)
        {
            string result = HexString.FromBytes(Compute(input));
            return result;
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