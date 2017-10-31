using System;
using System.Diagnostics;
using HashLib;

namespace Nevermind.Core.Encoding
{
    [DebuggerStepThrough]
    public class Keccak : IEquatable<Keccak>
    {
        private static readonly IHash Hash = HashFactory.Crypto.SHA3.CreateKeccak256();

        public Keccak(Hex hex)
        {
            if (hex.ByteLength != 32)
            {
                throw new ArgumentException("Keccak must be 32 bytes", nameof(hex));
            }

            Bytes = hex;
        }

        public Keccak(byte[] bytes)
        {
            if (bytes.Length != 32)
            {
                throw new ArgumentException("Keccak must be 32 bytes", nameof(bytes));
            }

            Bytes = bytes;
        }

        /// <returns>
        ///     <string>0xc5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470</string>
        /// </returns>
        public static Keccak OfAnEmptyString { get; } = InternalCompute(new byte[] { });

        /// <returns>
        ///     <string>0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347</string>
        /// </returns>
        public static Keccak OfAnEmptySequenceRlp { get; } = InternalCompute(new byte[] { 192 });

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

        [DebuggerStepThrough]
        public static Keccak Compute(Rlp rlp)
        {
            return new Keccak(Hash.ComputeBytes(rlp.Bytes).GetBytes());
        }

        [DebuggerStepThrough]
        public static Keccak Compute(byte[] input)
        {
            if (input == null || input.Length == 0)
            {
                return OfAnEmptyString;
            }

            return new Keccak(Hash.ComputeBytes(input).GetBytes());
        }

        private static Keccak InternalCompute(byte[] input)
        {
            return new Keccak(Hash.ComputeBytes(input).GetBytes());
        }

        [DebuggerStepThrough]
        public static Keccak Compute(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return OfAnEmptyString;
            }

            return InternalCompute(System.Text.Encoding.UTF8.GetBytes(input));
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
            return obj.GetType() == GetType() && Equals((Keccak)obj);
        }

        public override int GetHashCode()
        {
            return Bytes[0] ^ Bytes[31];
        }

        public static bool operator ==(Keccak a, Keccak b)
        {
            if (ReferenceEquals(a, b))
            {
                return true;
            }

            if (ReferenceEquals(a, null) || ReferenceEquals(b, null))
            {
                return false;
            }

            return Sugar.Bytes.UnsafeCompare(a.Bytes, b.Bytes);
        }

        public static bool operator !=(Keccak a, Keccak b)
        {
            return !(a == b);
        }
    }
}