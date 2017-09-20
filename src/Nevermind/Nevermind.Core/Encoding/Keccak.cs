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

        /// <returns>
        ///     <string>0xc5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470</string>
        /// </returns>
        public static Keccak OfAnEmptyString { get; } = Compute("");

        /// <returns>
        ///     <string>0x0000000000000000000000000000000000000000000000000000000000000000</string>
        /// </returns>
        public static Keccak Zero { get; } = new Keccak(new byte[32]);

        public byte[] Bytes { get; }

        public override string ToString()
        {
            return string.Concat("0x", HexString.FromBytes(Bytes));
        }

        //public override string ToString()
        //{
        //    return HexString.FromBytes(Bytes);
        //}

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