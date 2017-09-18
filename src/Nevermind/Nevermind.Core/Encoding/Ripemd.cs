using HashLib;

namespace Nevermind.Core.Encoding
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
            return HexString.FromBytes(Compute(input));
        }

        public static byte[] Compute(string input)
        {
            return Compute(System.Text.Encoding.UTF8.GetBytes(input));
        }

        public static string ComputeString(string input)
        {
            return HexString.FromBytes(Compute(HexString.ToBytes(input)));
        }
    }
}