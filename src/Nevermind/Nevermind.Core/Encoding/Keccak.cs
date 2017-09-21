using System;
using HashLib;

namespace Nevermind.Core.Encoding
{
    public class Keccak : IEquatable<Keccak>
    {
        private static readonly IHash Hash = HashFactory.Crypto.SHA3.CreateKeccak256();

        public Keccak(byte[] bytes)
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
            return Hex.FromBytes(Bytes, true);
        }

        public string ToString(bool withZeroX)
        {
            return Hex.FromBytes(Bytes, withZeroX);
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

        public bool Equals(Keccak other)
        {
            if (ReferenceEquals(null, other)) return false;
            if (ReferenceEquals(this, other)) return true;

            // timing attacks? probably not
            for (int i = 0; i < 32; i++)
            {
                if (other.Bytes[i] != Bytes[i])
                {
                    return false;
                }
            }

            return true;
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            if (obj.GetType() != this.GetType()) return false;
            return Equals((Keccak) obj);
        }

        public override int GetHashCode()
        {
            return (Bytes != null ? Bytes.GetHashCode() : 0);
        }
    }
}