// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
            List<AccessListItemForRpc> result = new();
            AccessListItemForRpc? current = null;
            foreach (AccessListItem element in accessList.Raw)
            {
                switch (element)
                {
                    case AccessListItem.Address address:
                        {
                            if (current is not null)
                            {
                                result.Add(current);
                            }
                            current = new AccessListItemForRpc(address.Value, new UInt256[] { });
                            break;
                        }
                    case AccessListItem.StorageKey storageKey:
                        {
                            current!.StorageKeys!.Add(storageKey.Value);
                            break;
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
    }
}
