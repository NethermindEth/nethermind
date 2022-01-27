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

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.AccountAbstraction.Data
{
    public class UserOperationAccessList
    {
        public static UserOperationAccessList Empty = new(new Dictionary<Address, HashSet<UInt256>>());

        public UserOperationAccessList(IDictionary<Address, HashSet<UInt256>> data)
        {
            Data = data;
        }

        public IDictionary<Address, HashSet<UInt256>> Data { get; set; }

        public static IDictionary<Address, HashSet<UInt256>> CombineAccessLists(
            IDictionary<Address, HashSet<UInt256>> accessList1, IDictionary<Address, HashSet<UInt256>> accessList2)
        {
            foreach (KeyValuePair<Address, HashSet<UInt256>> kv in accessList2)
                if (accessList1.TryGetValue(kv.Key, out HashSet<UInt256>? value))
                    value.UnionWith(kv.Value);
                else
                    accessList1[kv.Key] = kv.Value;

            return accessList1;
        }

        public bool AccessListContains(IDictionary<Address, HashSet<UInt256>> accessList2)
        {
            foreach (KeyValuePair<Address, HashSet<UInt256>> kv in accessList2)
                if (Data.TryGetValue(kv.Key, out HashSet<UInt256>? value))
                {
                    if (!value.IsSupersetOf(kv.Value)) return false;
                }
                else
                    return false;

            return true;
        }

        public bool AccessListOverlaps(IDictionary<Address, HashSet<UInt256>> accessList2)
        {
            foreach (KeyValuePair<Address, HashSet<UInt256>> kv in Data)
                if (accessList2.TryGetValue(kv.Key, out HashSet<UInt256>? value))
                    if (value.Overlaps(kv.Value))
                        return true;

            return false;
        }
    }
}
