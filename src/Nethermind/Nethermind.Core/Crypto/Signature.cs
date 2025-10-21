// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Nethermind.Core.Attributes;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.Core.Crypto
{
    public class Signature : MemoryManager<byte>, IEquatable<Signature>
    {
        public const int VOffset = 27;
        public const int Size = 65;
        private Vector512<byte> _signature;

        public Signature(ReadOnlySpan<byte> bytes, int recoveryId)
        {
            ArgumentOutOfRangeException.ThrowIfNotEqual(bytes.Length, 64);

            bytes.CopyTo(Bytes);
            V = (ulong)recoveryId + VOffset;
        }

        public Signature(ReadOnlySpan<byte> bytes)
        {
            ArgumentOutOfRangeException.ThrowIfNotEqual(bytes.Length, 65);

            bytes[..64].CopyTo(Bytes);
            V = bytes[64];
        }

        public Signature(ReadOnlySpan<byte> r, ReadOnlySpan<byte> s, ulong v)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(v, (ulong)VOffset);

            Span<byte> span = Bytes;
            r.CopyTo(span.Slice(32 - r.Length, r.Length));
            s.CopyTo(span.Slice(64 - s.Length, s.Length));
            V = v;
        }

        public Signature(in UInt256 r, in UInt256 s, ulong v)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(v, (ulong)VOffset);

            Span<byte> span = Bytes;
            r.ToBigEndian(span.Slice(0, 32));
            s.ToBigEndian(span.Slice(32, 32));

            V = v;
        }

        public Signature(string hexString)
            : this(Core.Extensions.Bytes.FromHexString(hexString))
        {
        }
        public Span<byte> Bytes => MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref _signature, 1));
        public override Memory<byte> Memory => CreateMemory(64);

        public ulong V { get; set; }

        public ulong? ChainId => V < 35 ? null : (ulong?)(V + (V % 2) - 36) / 2;

        public byte RecoveryId => GetRecoveryId(V);

        public static byte GetRecoveryId(ulong v) => v <= VOffset + 1 ? (byte)(v - VOffset) : (byte)(1 - v % 2);

        public Memory<byte> R => Memory.Slice(0, 32);
        public ReadOnlySpan<byte> RAsSpan => Bytes.Slice(0, 32);
        public Memory<byte> S => Memory.Slice(32, 32);
        public ReadOnlySpan<byte> SAsSpan => Bytes.Slice(32, 32);

        [Todo("Change signature to store 65 bytes and just slice it for normal Bytes.")]
        public byte[] BytesWithRecovery
        {
            get
            {
                var result = new byte[65];
                Bytes.CopyTo(result);
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
            if (other is null) return false;
            if (ReferenceEquals(this, other)) return true;
            return _signature == other._signature && V == other.V;
        }

        public override bool Equals(object? obj)
        {
            if (obj is null) return false;
            if (ReferenceEquals(this, obj)) return true;
            if (obj.GetType() != GetType()) return false;
            return Equals((Signature)obj);
        }

        public override int GetHashCode()
        {
            return MemoryMarshal.Read<int>(Bytes);
        }

        public void Dispose() { }
        protected override void Dispose(bool disposing) { }
        public override Span<byte> GetSpan() => Bytes;
        public override MemoryHandle Pin(int elementIndex = 0) => default;
        public override void Unpin() { }
    }
}
