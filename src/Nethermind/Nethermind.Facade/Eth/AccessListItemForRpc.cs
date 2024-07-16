// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Eip2930;
using Nethermind.Int256;
using Nethermind.Serialization.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nethermind.Facade.Eth
{
    public struct AccessListItemForRpc : IEquatable<AccessListItemForRpc>
    {
        [JsonConstructor]
        public AccessListItemForRpc() { }

        public AccessListItemForRpc(Address address, IEnumerable<UInt256>? storageKeys)
        {
            Address = address;
            StorageKeys = storageKeys;
        }

        public Address Address { get; set; }
        [JsonConverter(typeof(StorageCellIndexConverter))]
        public IEnumerable<UInt256>? StorageKeys { get; set; }

        public static IEnumerable<AccessListItemForRpc> FromAccessList(AccessList accessList) =>
            accessList.Select(tuple => new AccessListItemForRpc(tuple.Address, tuple.StorageKeys));

        public static AccessList ToAccessList(IEnumerable<AccessListItemForRpc> accessList)
        {
            AccessList.Builder builder = new();
            foreach (AccessListItemForRpc accessListItem in accessList)
            {
                builder.AddAddress(accessListItem.Address);
                if (accessListItem.StorageKeys is not null)
                {
                    foreach (UInt256 index in accessListItem.StorageKeys)
                    {
                        builder.AddStorage(index);
                    }
                }
            }

            return builder.Build();
        }

        public readonly bool Equals(AccessListItemForRpc other) => Equals(Address, other.Address) && StorageKeys.NullableSequenceEqual(other.StorageKeys);
        public override readonly bool Equals(object? obj) => obj is AccessListItemForRpc other && Equals(other);
        public override readonly int GetHashCode() => HashCode.Combine(Address, StorageKeys);
    }
}
