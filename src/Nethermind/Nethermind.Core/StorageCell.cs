// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.Core
{
    [DebuggerDisplay("{Address}->{Index}")]
    public readonly struct StorageCell : IEquatable<StorageCell>, IHash64, IHash64bit<StorageCell>
    {
        public static GenericEqualityComparer<StorageCell> EqualityComparer { get; } = new();
        private readonly AddressAsKey _address;
        private readonly UInt256 _index;
        private readonly bool _isHash;

        public Address Address => _address.Value;
        public bool IsHash => _isHash;
        public UInt256 Index => _index;

        public readonly ValueHash256 Hash => _isHash ? Unsafe.As<UInt256, ValueHash256>(ref Unsafe.AsRef(in _index)) : GetHash();

        private readonly ValueHash256 GetHash()
        {
            Span<byte> key = stackalloc byte[32];
            Index.ToBigEndian(key);
            return KeccakCache.Compute(key);
        }

        public StorageCell(Address address, in UInt256 index)
        {
            _address = address;
            _index = index;
        }

        public StorageCell(Address address, ValueHash256 hash)
        {
            _address = address;
            _index = Unsafe.As<ValueHash256, UInt256>(ref hash);
            _isHash = true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly bool Equals(in StorageCell other)
        {
            if (_isHash != other._isHash)
            {
                return false;
            }

            if (Unsafe.As<UInt256, Vector256<byte>>(ref Unsafe.AsRef(in _index)) !=
                Unsafe.As<UInt256, Vector256<byte>>(ref Unsafe.AsRef(in other._index)))
            {
                return false;
            }

            Address? a = _address.Value;
            Address? b = other._address.Value;
            if (ReferenceEquals(a, b))
            {
                return true;
            }

            if (a is null || b is null)
            {
                return false;
            }

            ref byte ab = ref MemoryMarshal.GetReference(a.Bytes);
            ref byte bb = ref MemoryMarshal.GetReference(b.Bytes);
            return Unsafe.As<byte, Vector128<byte>>(ref ab) == Unsafe.As<byte, Vector128<byte>>(ref bb)
                && Unsafe.As<byte, uint>(ref Unsafe.Add(ref ab, 16)) == Unsafe.As<byte, uint>(ref Unsafe.Add(ref bb, 16));
        }

        public readonly bool Equals(StorageCell other) => Equals(in other);

        public readonly override bool Equals(object? obj)
        {
            if (obj is null)
            {
                return false;
            }

            return obj is StorageCell address && Equals(address);
        }

        public readonly override int GetHashCode()
        {
            int hash = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in _index), 1)).FastHash();
            return hash ^ (_address.Value?.GetHashCode() ?? 0);
        }

        /// <summary>
        /// Returns a 64-bit hash code with good distribution for high-performance caching.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly long GetHashCode64()
        {
            // Hash the 32-byte UInt256 Index to 64 bits
            ref byte indexStart = ref Unsafe.As<UInt256, byte>(ref Unsafe.AsRef(in _index));
            long indexHash = SpanExtensions.FastHash64For32Bytes(ref indexStart);

            // Hash the 20-byte Address to 64 bits and combine
            Address? address = _address.Value;
            if (address is null)
            {
                return indexHash;
            }

            return indexHash ^ address.GetHashCode64();
        }

        public readonly override string ToString()
        {
            return $"{_address.Value}.{Index}";
        }
    }
}
