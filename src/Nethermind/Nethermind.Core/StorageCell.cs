// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Core
{
    [DebuggerDisplay("{Address}->{Index}")]
    public readonly struct StorageCell : IEquatable<StorageCell>
    {
        private readonly ValueHash256? _hash;
        private readonly UInt256? _index;

        public Address Address { get; }
        public UInt256 Index => _index ?? UInt256.Zero;

        public ValueHash256 Hash => _hash ?? GetHash();
        public bool IsHash => _index is null;

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

        public StorageCell(Address address, in ValueHash256 hash)
        {
            Address = address;
            _hash = hash;
            _index = null;
        }

        public bool Equals(StorageCell other) =>
            Address.Equals(other.Address) && (IsHash || other.IsHash ? Hash.Equals(other.Hash) : _index.Equals(other._index));

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
