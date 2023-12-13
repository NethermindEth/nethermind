// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
