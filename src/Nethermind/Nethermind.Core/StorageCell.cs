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
    public readonly struct StorageCell : IEquatable<StorageCell>
    {
        private enum Type : byte { Index, Hash, AddressOnly }
        private readonly UInt256 _index;
        private readonly Type _type;

        public Address Address { get; }
        public bool IsHash => _type == Type.Hash;
        public bool IsAddressOnly => _type == Type.AddressOnly;
        public UInt256 Index => _index;

        public ValueHash256 Hash => IsHash ? Unsafe.As<UInt256, ValueHash256>(ref Unsafe.AsRef(in _index)) : GetHash();

        private ValueHash256 GetHash()
        {
            Span<byte> key = stackalloc byte[32];
            Index.ToBigEndian(key);
            return KeccakCache.Compute(key);
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
            _type = Type.Hash;
        }

        public StorageCell(Address address)
        {
            Address = address;
            _type = Type.AddressOnly;
        }

        public bool Equals(StorageCell other) =>
            _type == other._type &&
            Unsafe.As<UInt256, Vector256<byte>>(ref Unsafe.AsRef(in _index)) == Unsafe.As<UInt256, Vector256<byte>>(ref Unsafe.AsRef(in other._index)) &&
            Address.Equals(other.Address);

        public override bool Equals(object? obj) => obj is StorageCell address && Equals(address);

        public override int GetHashCode()
        {
            int hash = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in _index), 1)).FastHash();
            return hash ^ Address.GetHashCode();
        }

        public override string ToString() => $"{Address}.{Index}";
    }
}
