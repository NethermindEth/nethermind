// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Core
{
    [DebuggerDisplay("{Address}->{Index}")]
    public readonly struct StorageCell : IEquatable<StorageCell>
    {
        private readonly UInt256 _index;
        private readonly bool _isHash;

        public Address Address { get; }
        public UInt256 Index => _index;

        public ValueHash256 Hash => _isHash ? Unsafe.As<UInt256, ValueHash256>(ref Unsafe.AsRef(in _index)) : GetHash();

        public bool IsHash => _isHash;

        private ValueHash256 GetHash()
        {
            Span<byte> key = stackalloc byte[32];
            Index.ToBigEndian(key);
            return ValueKeccak.Compute(key);
        }

        public StorageCell(Address address, in UInt256 index)
        {
            Address = address;
            _index = index;
        }

        public StorageCell(Address address, ValueHash256 hash)
        {
            Address = address;
            _index = Unsafe.As<ValueHash256, UInt256>(ref hash);
            _isHash = true;
        }

        public bool Equals(StorageCell other) => Address.Equals(other.Address) && Index.Equals(other.Index);

        public override bool Equals(object? obj)
        {
            if (ReferenceEquals(null, obj))
            {
                return false;
            }

            return obj is StorageCell address && Equals(address);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return (Address.GetHashCode() * 397) ^ Index.GetHashCode();
            }
        }

        public override string ToString()
        {
            return $"{Address}.{Index}";
        }
    }
}
