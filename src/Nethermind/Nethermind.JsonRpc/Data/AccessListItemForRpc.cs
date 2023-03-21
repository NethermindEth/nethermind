// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Eip2930;
using Nethermind.Int256;
using Nethermind.Serialization.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nethermind.JsonRpc.Data
{
    public class AccessListItemForRpc
    {
        [JsonConstructor]
        public AccessListItemForRpc() { }
        public AccessListItemForRpc(Address address, IReadOnlyCollection<UInt256>? storageKeys)
        {
            Address = address;
            StorageKeys = storageKeys?.ToArray() ?? Array.Empty<UInt256>();
        }

        public Address Address { get; set; }

        private UInt256[]? _storageKeys;
        [JsonConverter(typeof(StorageCellIndexConverter))]
        public UInt256[]? StorageKeys { get => _storageKeys ?? Array.Empty<UInt256>(); set => _storageKeys = value; }

        public static AccessListItemForRpc[] FromAccessList(AccessList accessList) =>
            accessList.Data.Select(kvp => new AccessListItemForRpc(kvp.Key, kvp.Value)).ToArray();

        public static AccessList ToAccessList(AccessListItemForRpc[] accessList)
        {
            AccessListBuilder accessListBuilder = new();
            for (int i = 0; i < accessList.Length; i++)
            {
                var accessListItem = accessList[i];
                accessListBuilder.AddAddress(accessListItem.Address);
                if (accessListItem.StorageKeys is not null)
                {
                    for (int j = 0; j < accessListItem.StorageKeys.Length; j++)
                    {
                        accessListBuilder.AddStorage(accessListItem.StorageKeys[j]);
                    }
                }
            }
            return accessListBuilder.ToAccessList();
        }
    }
}
