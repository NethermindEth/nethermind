// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using Nethermind.Int256;

namespace Nethermind.Core
{
    [DebuggerDisplay("{Address}->{Index}")]
    public readonly struct StorageCell : IEquatable<StorageCell>
    {
        public Address Address { get; }
        public UInt256 Index { get; }

        public bool IsTransient { get; }

        public StorageCell(Address address, in UInt256 index, bool isTransient = false)
        {
            Address = address;
            Index = index;
            IsTransient = isTransient;
        }

        public bool Equals(StorageCell other)
        {
            return Index.Equals(other.Index) && Address.Equals(other.Address);
        }

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
                return (Address.GetHashCode() * 397) ^ HashCode.Combine(Index, IsTransient);
            }
        }

        public override string ToString()
        {
            string isTransient = IsTransient ? "Transient" : "Persistent";
            return $"{Address}.{Index}.{isTransient}";
        }
    }
}
