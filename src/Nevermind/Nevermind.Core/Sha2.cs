using System.Text;
using HashLib;

namespace Nevermind.Core
{
    public static class Sha2
    {
        private static readonly IHash Hash = HashFactory.Crypto.CreateSHA256();

        public static byte[] Compute(byte[] input)
        {
            return Hash.ComputeBytes(input).GetBytes();
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