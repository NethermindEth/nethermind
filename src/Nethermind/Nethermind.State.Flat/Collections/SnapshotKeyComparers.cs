// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Trie;

namespace Nethermind.State.Flat.Collections;

/// <summary>
/// Total-order comparers over the <see cref="HashedKey{T}"/> keys used by snapshot content. The trie-node
/// comparers reproduce the exact <c>(Hash256, TreePath)</c> tuple order the persistence layer sorts by, so a
/// sorted content can be persisted without re-sorting.
/// </summary>
public static class SnapshotKeyComparers
{
    public static readonly IComparer<HashedKey<Address>> Address = new AddressComparer();
    public static readonly IComparer<HashedKey<(Address, UInt256)>> Storage = new StorageComparer();
    public static readonly IComparer<HashedKey<TreePath>> StateNode = new StateNodeComparer();
    public static readonly IComparer<HashedKey<(Hash256, TreePath)>> StorageNode = new StorageNodeComparer();

    private sealed class AddressComparer : IComparer<HashedKey<Address>>
    {
        public int Compare(HashedKey<Address> x, HashedKey<Address> y) => x.Key.CompareTo(y.Key);
    }

    private sealed class StorageComparer : IComparer<HashedKey<(Address, UInt256)>>
    {
        public int Compare(HashedKey<(Address, UInt256)> x, HashedKey<(Address, UInt256)> y)
        {
            int c = x.Key.Item1.CompareTo(y.Key.Item1);
            return c != 0 ? c : x.Key.Item2.CompareTo(y.Key.Item2);
        }
    }

    private sealed class StateNodeComparer : IComparer<HashedKey<TreePath>>
    {
        public int Compare(HashedKey<TreePath> x, HashedKey<TreePath> y) => x.Key.CompareTo(y.Key);
    }

    private sealed class StorageNodeComparer : IComparer<HashedKey<(Hash256, TreePath)>>
    {
        public int Compare(HashedKey<(Hash256, TreePath)> x, HashedKey<(Hash256, TreePath)> y)
        {
            int c = x.Key.Item1.CompareTo(y.Key.Item1);
            return c != 0 ? c : x.Key.Item2.CompareTo(y.Key.Item2);
        }
    }
}
