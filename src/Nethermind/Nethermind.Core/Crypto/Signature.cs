/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Numerics;
using Nethermind.Core.Extensions;

namespace Nethermind.Core.Crypto
{
    /// <summary>
    /// TODO: can I mark it as a EIP155 signature? otherwise it should not be accepted with V > 28
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
            V = recoveryId + 27;
        }

        public Signature(byte[] bytes)
        {
            if (bytes.Length != 65)
            {
                throw new ArgumentException();
            }

            Buffer.BlockCopy(bytes, 0, Bytes, 0, 64);
            V = bytes[64];
        }

        public Signature(byte[] r, byte[] s, int v)
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

        public Signature(BigInteger r, BigInteger s, int v)
        {
            if (v < 27)
            {
                throw new ArgumentException(nameof(v));
            }

            byte[] rBytes = r.ToBigEndianByteArray();
            byte[] sBytes = s.ToBigEndianByteArray();

            Buffer.BlockCopy(rBytes.PadLeft(32), 0, Bytes, 0, 32);
            Buffer.BlockCopy(sBytes.PadLeft(32), 0, Bytes, 32, 32);
            V = v;
        }

        public Signature(string hexString)
            : this(Extensions.Bytes.FromHexString(hexString))
        {
        }

        public byte[] Bytes { get; } = new byte[64];
        public int V { get; set; }
        
        /* is it correct for Goerli? */
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
            string vString = V.ToString("X").ToLower();            
            return string.Concat(Bytes.ToHexString(true), vString.Length % 2 == 0 ? vString : string.Concat("0", vString));
        }

        public bool Equals(Signature other)
        {
            if (ReferenceEquals(null, other)) return false;
            if (ReferenceEquals(this, other)) return true;
            return Extensions.Bytes.AreEqual(Bytes, other.Bytes) && V == other.V;
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            if (obj.GetType() != GetType()) return false;
            return Equals((Signature)obj);
        }

        public override int GetHashCode()
        {
            return Bytes.GetXxHashCode() ^ V.GetHashCode();
        }
    }
}