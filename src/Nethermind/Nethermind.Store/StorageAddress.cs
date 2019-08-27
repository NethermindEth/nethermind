﻿/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Diagnostics;
using Nethermind.Core;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Store
{
    [DebuggerDisplay("{Address}->{Index}")]
    public struct StorageAddress : IEquatable<StorageAddress>
    {
        public Address Address { get; }
        public UInt256 Index { get; }

        public StorageAddress(Address address, UInt256 index)
        {
            Address = address;
            Index = index;
        }

        public bool Equals(StorageAddress other)
        {
            return Equals(Address, other.Address) && Index.Equals(other.Index);
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj))
            {
                return false;
            }
            return obj is StorageAddress address && Equals(address);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return ((Address != null ? Address.GetHashCode() : 0) * 397) ^ Index.GetHashCode();
            }
        }

        public override string ToString()
        {
            return $"{Address}.{Index}";
        }
    }
}