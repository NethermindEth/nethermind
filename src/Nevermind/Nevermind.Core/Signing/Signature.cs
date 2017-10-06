using System;
using System.Numerics;
using Nevermind.Core.Encoding;
using Nevermind.Core.Sugar;

namespace Nevermind.Core.Signing
{
    public class Signature
    {
        public Signature(byte[] bytes, int recoveryId)
        {
            if (bytes.Length != 64)
            {
                throw new ArgumentException();
            }

            Buffer.BlockCopy(bytes, 0, Bytes, 0, 64);
            V = (byte) (recoveryId + 27);
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

        public Signature(BigInteger r, BigInteger s, byte v)
        {
            byte[] rBytes = r.ToBigEndianByteArray();
            byte[] sBytes = s.ToBigEndianByteArray();

            Buffer.BlockCopy(rBytes, 0, Bytes, 0, 32);
            Buffer.BlockCopy(sBytes, 0, Bytes, 32, 32);
            V = v;
        }

        public Signature(string hexString)
            : this(Hex.ToBytes(hexString))
        {
        }

        public byte[] Bytes { get; } = new byte[64];
        public byte V { get; }
        public int RecoveryId => V - 27;
        public byte[] R => Bytes.Slice(0, 32);
        public byte[] S => Bytes.Slice(32, 32);

        public override string ToString()
        {
            return string.Concat(Hex.FromBytes(Bytes, true), Hex.FromBytes(new[] {V}, false));
        }
    }
}