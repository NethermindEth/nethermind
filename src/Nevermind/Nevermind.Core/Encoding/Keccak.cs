using HashLib;

namespace Nevermind.Core.Encoding
{
    public class Keccak
    {
        private static readonly IHash Hash = HashFactory.Crypto.SHA3.CreateKeccak256();

        private Keccak(byte[] bytes)
        {
            Bytes = bytes;
        }

        public static Keccak OfEmptyString { get; } = Compute("c5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470");

        public static Keccak Zero { get; }  = new Keccak(new byte[32]);

        public byte[] Bytes { get; }

        public override string ToString()
        {
            return HexString.FromBytes(Bytes);
        }

        public static Keccak Compute(Rlp rlp)
        {
            return new Keccak(Hash.ComputeBytes(rlp.Bytes).GetBytes());
        }

        public static Keccak Compute(byte[] input)
        {
            return new Keccak(Hash.ComputeBytes(input).GetBytes());
        }

        public static Keccak Compute(string input)
        {
            return Compute(System.Text.Encoding.UTF8.GetBytes(input));
        }
    }
}