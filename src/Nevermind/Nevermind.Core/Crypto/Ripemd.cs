using HashLib;

namespace Nevermind.Core.Crypto
{
    public static class Ripemd
    {
        private static readonly IHash Hash = HashFactory.Crypto.CreateRIPEMD160();

        public static byte[] Compute(byte[] input)
        {
            return Hash.ComputeBytes(input).GetBytes();
        }

        public static string ComputeString(byte[] input)
        {
            return Hex.FromBytes(Compute(input), false);
        }

        public static byte[] Compute(string input)
        {
            return Compute(System.Text.Encoding.UTF8.GetBytes(input));
        }

        public static string ComputeString(string input)
        {
            return ComputeString(System.Text.Encoding.UTF8.GetBytes(input));
        }
    }
}