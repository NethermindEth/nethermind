//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Runtime.InteropServices;
using Nethermind.Core.Attributes;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.Core.Crypto
{
    public class Signature : IEquatable<Signature>
    {
        public const int VOffset = 27;
        
        public Signature(ReadOnlySpan<byte> bytes, int recoveryId)
        {
            if (bytes.Length != 64)
            {
                throw new ArgumentException();
            }

            bytes.CopyTo(Bytes.AsSpan());
            V = (ulong)recoveryId + VOffset;
        }

        public Signature(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length != 65)
            {
                throw new ArgumentException();
            }

            bytes.Slice(0, 64).CopyTo(Bytes.AsSpan());
            V = bytes[64];
        }

        public Signature(ReadOnlySpan<byte> r, ReadOnlySpan<byte> s, ulong v)
        {
            if (v < VOffset)
            {
                throw new ArgumentException(nameof(v));
            }

            r.CopyTo(Bytes.AsSpan().Slice(32 - r.Length, r.Length));
            s.CopyTo(Bytes.AsSpan().Slice(64 - s.Length, s.Length));
            V = v;
        }

        public Signature(UInt256 r, UInt256 s, ulong v)
        {
            if (v < VOffset)
            {
                throw new ArgumentException(nameof(v));
            }

            r.ToBigEndian(Bytes.AsSpan().Slice(0, 32));
            s.ToBigEndian(Bytes.AsSpan().Slice(32, 32));

            V = v;
        }

        public Signature(string hexString)
            : this(Core.Extensions.Bytes.FromHexString(hexString))
        {
        }

        public byte[] Bytes { get; } = new byte[64];
        public ulong V { get; set; }

        public ulong? ChainId => V < 35 ? null : (ulong?) (V + (V % 2) - 36) / 2;

        public byte RecoveryId => V <= VOffset + 1 ? (byte) (V - VOffset) : (byte) (1 - V % 2);

        public byte[] R => Bytes.Slice(0, 32);
        public Span<byte> RAsSpan => Bytes.AsSpan().Slice(0, 32);
        public byte[] S => Bytes.Slice(32, 32);
        public Span<byte> SAsSpan => Bytes.AsSpan().Slice(32, 32);

        [Todo("Change signature to store 65 bytes and just slice it for normal Bytes.")]
        public byte[] BytesWithRecovery
        {
            get
            {
                var result = new byte[65];
                Array.Copy(Bytes, result, 64);
                result[64] = RecoveryId;
                return result;
            }
        }

        public override string ToString()
        {
            string vString = V.ToString("X").ToLower();
            return string.Concat(Bytes.ToHexString(true), vString.Length % 2 == 0 ? vString : string.Concat("0", vString));
        }

        public bool Equals(Signature? other)
        {
            if (ReferenceEquals(null, other)) return false;
            if (ReferenceEquals(this, other)) return true;
            return Core.Extensions.Bytes.AreEqual(Bytes, other.Bytes) && V == other.V;
        }

        public override bool Equals(object? obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            if (obj.GetType() != GetType()) return false;
            return Equals((Signature) obj);
        }

        public override int GetHashCode()
        {
            return MemoryMarshal.Read<int>(Bytes);
        }
    }
}
