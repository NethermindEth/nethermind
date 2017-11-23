using System;
using System.Numerics;
using Nevermind.Core.Extensions;

namespace Nevermind.Core.Crypto
{
    /// <summary>
    /// Can I mark it as a EIP155 signature? otherwise it should not be accepted with V > 28
    /// </summary>
    public class Signature : IEquatable<Signature>
    {
        public Signature(byte[] bytes, int recoveryId)
        {
            if (bytes.Length != 64)
            {
                throw new ArgumentException();
            }

            Buffer.BlockCopy(bytes, 0, Bytes, 0, 64);
            V = (byte)(recoveryId + 27);
        }

        private Signature(byte[] bytes)
        {
            if (bytes.Length != 65)
            {
                throw new ArgumentException();
            }

            Buffer.BlockCopy(bytes, 0, Bytes, 0, 64);
            V = bytes[64];
        }

        public Signature(byte[] r, byte[] s, byte v)
        {
            if (r.Length != 32)
            {
                throw new ArgumentException(nameof(r));
            }

            if (s.Length != 32)
            {
                throw new ArgumentException(nameof(s));
            }

            if (v < 27)
            {
                throw new ArgumentException(nameof(v));
            }

            Buffer.BlockCopy(r, 0, Bytes, 0, 32);
            Buffer.BlockCopy(s, 0, Bytes, 32, 32);
            V = v;
        }

        public Signature(BigInteger r, BigInteger s, byte v)
        {
            if (v < 27)
            {
                throw new ArgumentException(nameof(v));
            }

            byte[] rBytes = r.ToBigEndianByteArray();
            byte[] sBytes = s.ToBigEndianByteArray();

            Buffer.BlockCopy(Extensions.Bytes.PadLeft(rBytes, 32), 0, Bytes, 0, 32);
            Buffer.BlockCopy(Extensions.Bytes.PadLeft(sBytes, 32), 0, Bytes, 32, 32);
            V = v;
        }

        public Signature(string hexString)
            : this(Hex.ToBytes(hexString))
        {
        }

        public byte[] Bytes { get; } = new byte[64];
        public byte V { get; }
        public byte RecoveryId
        {
            get
            {
                if (V <= 28)
                {
                    return (byte)(V - 27);
                }

                return (byte)(1 - V % 2);
            }
        }

        public byte[] R => Bytes.Slice(0, 32);
        public byte[] S => Bytes.Slice(32, 32);

        public override string ToString()
        {
            return string.Concat(Hex.FromBytes(Bytes, true), Hex.FromBytes(new[] { V }, false));
        }

        public bool Equals(Signature other)
        {
            if (ReferenceEquals(null, other)) return false;
            if (ReferenceEquals(this, other)) return true;
            return Extensions.Bytes.UnsafeCompare(Bytes, other.Bytes) && V == other.V;
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            if (obj.GetType() != this.GetType()) return false;
            return Equals((Signature)obj);
        }

        public override int GetHashCode()
        {
            return Bytes.GetXxHashCode() ^ V.GetHashCode();
        }
    }
}