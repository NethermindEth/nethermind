// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Eip2930;
using Nethermind.Int256;
using Nethermind.Serialization.Json;
using Newtonsoft.Json;

namespace Nethermind.JsonRpc.Data
{
    public class AccessListItemForRpc
    {
        public AccessListItemForRpc(Address address, IReadOnlyCollection<UInt256>? storageKeys)
        {
            Address = address;
            StorageKeys = storageKeys?.ToList() ?? new List<UInt256>();
        }

        public Address Address { get; set; }

        [JsonProperty(ItemConverterType = typeof(StorageCellIndexConverter))]
        public List<UInt256>? StorageKeys { get; set; }

        public static AccessListItemForRpc[] FromAccessList(AccessList accessList)
        {
            if (accessList.OrderQueue is null)
            {
                return accessList.Data
                    .Select(kvp => new AccessListItemForRpc(kvp.Key, kvp.Value))
                    .ToArray();
            }

            List<AccessListItemForRpc> result = new();
            AccessListItemForRpc? current = null;
            foreach (object element in accessList.OrderQueue)
            {
                switch (element)
                {
                    case Address address:
                    {
                        if (current is not null)
                        {
                            result.Add(current);
                        }
                        current = new AccessListItemForRpc(address, new UInt256[]{ });
                        break;
                    }
                    case UInt256 storageKey:
                    {
                        current!.StorageKeys!.Add(storageKey);
                        break;
                    }
                    default:
                    {
                        throw new ArgumentException($"{nameof(accessList)} values are not from the expected type");
                    }
                }
            }
            if (current is not null)
            {
                result.Add(current);
            }

            return result.ToArray();
        }

        public static AccessList ToAccessList(AccessListItemForRpc[] accessList)
        {
            AccessListBuilder accessListBuilder = new();
            foreach (AccessListItemForRpc accessListItem in accessList)
            {
                accessListBuilder.AddAddress(accessListItem.Address);
                if (accessListItem.StorageKeys is not null)
                {
                    foreach (UInt256 index in accessListItem.StorageKeys)
                    {
                        accessListBuilder.AddStorage(index);
                    }
                }
            }
            return accessListBuilder.ToAccessList();
        }
    }
}
