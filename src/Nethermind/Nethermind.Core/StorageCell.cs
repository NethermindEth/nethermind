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
        private readonly UInt256 _index;
        public Address Address { get; }
        public UInt256 Index => _index;

        public StorageCell(Address address, in UInt256 index)
        {
            Address = address;
            _index = index;
        }

        public StorageCell(Address address, ValueHash256 hash)
        {
            Address = address;
            _index = Unsafe.As<ValueHash256, UInt256>(ref hash);
        }

        public bool Equals(StorageCell other) =>
            Unsafe.As<UInt256, Vector256<byte>>(ref Unsafe.AsRef(in _index)) == Unsafe.As<UInt256, Vector256<byte>>(ref Unsafe.AsRef(in other._index)) &&
            Address.Equals(other.Address);

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
            return hash ^ Address.GetHashCode();
        }

        public override string ToString()
        {
            return $"{Address}.{Index}";
        }
    }
}
