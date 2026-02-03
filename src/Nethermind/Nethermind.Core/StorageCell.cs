// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.Core
{
    [DebuggerDisplay("{Address}->{Index}")]
    public readonly struct StorageCell : IEquatable<StorageCell>, IHash64
    {
        public readonly UInt256 Index;
        private readonly bool _isHash;

        public Address Address { get; }
        public bool IsHash => _isHash;

        public readonly ValueHash256 Hash => _isHash ? Unsafe.As<UInt256, ValueHash256>(ref Unsafe.AsRef(in Index)) : GetHash();

        private readonly ValueHash256 GetHash()
        {
            Span<byte> key = stackalloc byte[32];
            Index.ToBigEndian(key);
            return KeccakCache.Compute(key);
        }

        public StorageCell(Address address, in UInt256 index)
        {
            Address = address;
            Index = index;
        }

        public StorageCell(Address address, ValueHash256 hash)
        {
            Address = address;
            Index = Unsafe.As<ValueHash256, UInt256>(ref hash);
            _isHash = true;
        }

        public readonly bool Equals(StorageCell other) =>
            _isHash == other._isHash &&
            Unsafe.As<UInt256, Vector256<byte>>(ref Unsafe.AsRef(in Index)) == Unsafe.As<UInt256, Vector256<byte>>(ref Unsafe.AsRef(in other.Index)) &&
            Address.Equals(other.Address);

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
            int hash = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in Index), 1)).FastHash();
            return hash ^ Address.GetHashCode();
        }

        /// <summary>
        /// Returns a 64-bit hash code with good distribution for high-performance caching.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly long GetHashCode64()
        {
            // Hash the 32-byte UInt256 Index to 64 bits
            ref byte indexStart = ref Unsafe.As<UInt256, byte>(ref Unsafe.AsRef(in Index));
            long indexHash = SpanExtensions.FastHash64For32Bytes(ref indexStart);

            // Hash the 20-byte Address to 64 bits and combine
            ref byte addrStart = ref MemoryMarshal.GetReference(Address.Bytes);
            long addrHash = SpanExtensions.FastHash64For20Bytes(ref addrStart);

            // Combine with multiplication to mix bits (golden ratio constant)
            return indexHash ^ (addrHash * unchecked((long)0x9E3779B97F4A7C15));
        }

        public readonly override string ToString()
        {
            return $"{Address}.{Index}";
        }
    }
}
