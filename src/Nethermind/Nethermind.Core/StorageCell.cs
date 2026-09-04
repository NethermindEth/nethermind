// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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
            if (!Extensions.Bytes.AreEqual32(
                    ref Unsafe.As<UInt256, byte>(ref Unsafe.AsRef(in _index)),
                    ref Unsafe.As<UInt256, byte>(ref Unsafe.AsRef(in other._index))))
                return false;

            return _address.Equals(in other._address);
        }

        public bool Equals(StorageCell other) => Equals(in other);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long GetHashCode64() => SpanExtensions.FastHash64ForAddressAndSlot(
            ref MemoryMarshal.GetReference(_address.Value.Bytes),
            ref Unsafe.As<UInt256, byte>(ref Unsafe.AsRef(in _index)));

        public override bool Equals(object? obj)
        {
            if (obj is null)
            {
                return false;
            }

            return obj is StorageCell address && Equals(address);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override int GetHashCode()
        {
            ulong hash = (ulong)GetHashCode64();
            return (int)(hash ^ (hash >> 32));
        }

        public override string ToString() => $"{_address.Value}.{Index}";
    }
}
