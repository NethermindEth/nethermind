// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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

            bytes[..64].CopyTo(Bytes.AsSpan());
            V = bytes[64];
        }

        public Signature(ReadOnlySpan<byte> r, ReadOnlySpan<byte> s, ulong v)
        {
            if (v < VOffset)
            {
                throw new ArgumentException(nameof(v));
            }

            r.CopyTo(Bytes.AsSpan(32 - r.Length, r.Length));
            s.CopyTo(Bytes.AsSpan(64 - s.Length, s.Length));
            V = v;
        }

        public Signature(in UInt256 r, in UInt256 s, ulong v)
        {
            if (v < VOffset)
            {
                throw new ArgumentException(nameof(v));
            }

            r.ToBigEndian(Bytes.AsSpan(0, 32));
            s.ToBigEndian(Bytes.AsSpan(32, 32));

            V = v;
        }

        public Signature(string hexString)
            : this(Core.Extensions.Bytes.FromHexString(hexString))
        {
        }

        public byte[] Bytes { get; } = new byte[64];
        public ulong V { get; set; }

        public ulong? ChainId => V < 35 ? null : (ulong?)(V + (V % 2) - 36) / 2;

        public byte RecoveryId => V <= VOffset + 1 ? (byte)(V - VOffset) : (byte)(1 - V % 2);

        public byte[] R => Bytes.Slice(0, 32);
        public Span<byte> RAsSpan => Bytes.AsSpan(0, 32);
        public byte[] S => Bytes.Slice(32, 32);
        public Span<byte> SAsSpan => Bytes.AsSpan(32, 32);

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
            return Equals((Signature)obj);
        }

        public override int GetHashCode()
        {
            return MemoryMarshal.Read<int>(Bytes);
        }
    }
}
