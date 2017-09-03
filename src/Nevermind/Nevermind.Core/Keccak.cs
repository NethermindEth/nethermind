using System.Text;
using HashLib;

namespace Nevermind.Core
{
    public class Keccak
    {
        private static readonly IHash Hash = HashFactory.Crypto.SHA3.CreateKeccak256();

        private Keccak(byte[] bytes)
        {
            Bytes = bytes;
        }

        public byte[] Bytes { get; }

        public override string ToString()
        {
            return HexString.FromBytes(Bytes);
        }

        public static Keccak Compute(byte[] input)
        {
            return new Keccak(Hash.ComputeBytes(input).GetBytes());
        }

        public static Keccak Compute(string input)
        {
            return Compute(Encoding.UTF8.GetBytes(input));
        }
    }
}