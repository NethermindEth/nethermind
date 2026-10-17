// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Int256;

namespace Nethermind.State.Flat.Collections;

// Key comparers for snapshot content. They are stateless
// structs passed as value-type generic arguments (see SortedMergeDictionary.BuildFromMerge), so every Compare
// call is devirtualized and inlined instead of interface-dispatched.

internal readonly struct AddressKeyComparer : IComparer<HashedKey<Address>>
{
    public int Compare(HashedKey<Address> x, HashedKey<Address> y) => x.Key.CompareTo(y.Key);
}

internal readonly struct StorageKeyComparer : IComparer<HashedKey<(Address, UInt256)>>
{
    public int Compare(HashedKey<(Address, UInt256)> x, HashedKey<(Address, UInt256)> y)
    {
        int c = x.Key.Item1.CompareTo(y.Key.Item1);
        return c != 0 ? c : x.Key.Item2.CompareTo(y.Key.Item2);
    }
}
