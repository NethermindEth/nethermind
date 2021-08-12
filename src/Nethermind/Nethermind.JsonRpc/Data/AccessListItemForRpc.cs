//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

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
            StorageKeys = storageKeys?.ToArray() ?? Array.Empty<UInt256>();
        }
        
        public Address Address { get; set; }
        
        [JsonProperty(ItemConverterType = typeof(StorageCellIndexConverter))]
        public UInt256[]? StorageKeys { get; set; }

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
