// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.Core
{
    [DebuggerDisplay("{Address}->{Index}")]
    public readonly struct StorageCell(Address address, in UInt256 index) : IEquatable<StorageCell>, IHash64bit<StorageCell>
    {
        public static GenericEqualityComparer<StorageCell> EqualityComparer { get; } = new();
        private readonly AddressAsKey _address = address;
        private readonly UInt256 _index = index;

        public Address Address => _address.Value;
        public UInt256 Index => _index;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Equals(in StorageCell other)
        {
            if (Unsafe.As<UInt256, Vector256<byte>>(ref Unsafe.AsRef(in _index)) !=
                Unsafe.As<UInt256, Vector256<byte>>(ref Unsafe.AsRef(in other._index)))
                return false;

            // Inline 20-byte Address comparison: avoids the Address.Equals call
            // that the JIT refuses to inline when called from deep inline chains
            // (e.g. SeqlockCache.TryGetValue). Address.Bytes is always exactly 20 bytes.
            Address a = _address.Value;
            Address b = other._address.Value;
            if (ReferenceEquals(a, b))
                return true;

            ref byte ab = ref MemoryMarshal.GetReference(a.Bytes);
            ref byte bb = ref MemoryMarshal.GetReference(b.Bytes);
            return Unsafe.As<byte, Vector128<byte>>(ref ab) == Unsafe.As<byte, Vector128<byte>>(ref bb)
                && Unsafe.As<byte, uint>(ref Unsafe.Add(ref ab, 16)) == Unsafe.As<byte, uint>(ref Unsafe.Add(ref bb, 16));
        }

        public bool Equals(StorageCell other) => Equals(in other);

        public long GetHashCode64()
            => SpanExtensions.FastHash64For32Bytes(ref Unsafe.As<UInt256, byte>(ref Unsafe.AsRef(in _index))) ^ _address.Value.GetHashCode64();

        public override bool Equals(object? obj)
        {
            if (obj is null)
            {
                return false;
            }

            return obj is StorageCell address && Equals(address);
        }

        public override int GetHashCode()
        {
            int hash = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in _index), 1)).FastHash();
            return hash ^ _address.Value.GetHashCode();
        }

        public override string ToString() => $"{_address.Value}.{Index}";
    }
}
