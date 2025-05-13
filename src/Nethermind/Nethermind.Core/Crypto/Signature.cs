// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Nethermind.Core.Attributes;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.Core.Crypto
{
    public class Signature : MemoryManager<byte>, IEquatable<Signature>
    {
        public const int Length = 65;

        public const int VOffset = 27;

        private readonly byte[] _signature = new byte[Length];

        ref readonly Vector512<byte> Vector => ref Unsafe.As<byte, Vector512<byte>>(ref MemoryMarshal.GetArrayDataReference(_signature));

        public Signature(ReadOnlySpan<byte> bytes, int recoveryId)
        {
            ArgumentOutOfRangeException.ThrowIfNotEqual(bytes.Length, 64);
            ArgumentOutOfRangeException.ThrowIfNegative(recoveryId);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(recoveryId, byte.MaxValue);

            bytes.CopyTo(_signature);
            _signature[64] = (byte)recoveryId;
            V = (ulong)recoveryId + VOffset;
        }

        public Signature(ReadOnlySpan<byte> bytes)
        {
            ArgumentOutOfRangeException.ThrowIfNotEqual(bytes.Length, 65);

            bytes.CopyTo(_signature);
            V = bytes[64];
        }

        public Signature(ReadOnlySpan<byte> r, ReadOnlySpan<byte> s, ulong v)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(v, (ulong)VOffset);

            Span<byte> span = _signature;
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
        public Span<byte> Bytes => _signature.AsSpan(0, 64);

        public override Memory<byte> Memory => CreateMemory(64);

        public ulong V { get; set; }

        public ulong? ChainId => V < 35 ? null : (ulong?)(V + (V % 2) - 36) / 2;

        public byte RecoveryId => V <= VOffset + 1 ? (byte)(V - VOffset) : (byte)(1 - V % 2);

        public Memory<byte> R => Memory.Slice(0, 32);
        public ReadOnlySpan<byte> RAsSpan => Bytes.Slice(0, 32);
        public Memory<byte> S => Memory.Slice(32, 32);
        public ReadOnlySpan<byte> SAsSpan => Bytes.Slice(32, 32);

        [Todo("Change signature to store 65 bytes and just slice it for normal Bytes.")]
        public ReadOnlySpan<byte> BytesWithRecovery => _signature.AsSpan();


        public override string ToString()
        {
            string vString = V.ToString("X").ToLower();
            return string.Concat(Bytes.ToHexString(true), vString.Length % 2 == 0 ? vString : string.Concat("0", vString));
        }

        public bool Equals(Signature? other)
        {
            if (other is null) return false;
            if (ReferenceEquals(this, other)) return true;
            return Vector == other.Vector && V == other.V;
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
