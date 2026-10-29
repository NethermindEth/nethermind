// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core.Collections;

namespace Nethermind.Core.BlockAccessLists;

/// <summary>The distinct EIP-7928 items of a block access list being built: its accounts and its storage keys.</summary>
/// <remarks>
/// Built from the log as the indexes complete, so block production can tell how many items a transaction adds
/// before the block's list is merged. A storage key counts once whether it is changed, read, or both.
/// </remarks>
public sealed class BlockAccessListItemSet
{
    private readonly HashSet<AddressAsKey> _accounts = new(GenericEqualityComparer.GetOptimized<AddressAsKey>());
    private readonly HashSet<StorageCell> _storageKeys = new(StorageCell.EqualityComparer);
    private readonly HashSet<AddressAsKey> _newAccounts = new(GenericEqualityComparer.GetOptimized<AddressAsKey>());
    private readonly HashSet<StorageCell> _newStorageKeys = new(StorageCell.EqualityComparer);

    public int ItemCount => _accounts.Count + _storageKeys.Count;

    public bool HasAccount(Address address) => _accounts.Contains(address);

    /// <summary>The number of items the entries of <paramref name="log"/> from <paramref name="start"/> on would add.</summary>
    public int CountNewItems(BlockAccessListLog log, int start)
    {
        _newAccounts.Clear();
        _newStorageKeys.Clear();
        Collector collector = new(this, add: false);
        log.VisitAccesses(start, ref collector);
        int count = _newAccounts.Count + _newStorageKeys.Count;
        _newAccounts.Clear();
        _newStorageKeys.Clear();
        return count;
    }

    /// <summary>Adds the items of the entries of <paramref name="log"/> from <paramref name="start"/> on.</summary>
    public void TrackAddedEntries(BlockAccessListLog log, int start)
    {
        Collector collector = new(this, add: true);
        log.VisitAccesses(start, ref collector);
    }

    public void Clear()
    {
        _accounts.ClearAndTrim();
        _storageKeys.ClearAndTrim();
    }

    private readonly struct Collector(BlockAccessListItemSet set, bool add) : BlockAccessListLog.IAccessVisitor
    {
        public void Account(Address address)
        {
            if (add) set._accounts.Add(address);
            else if (!set._accounts.Contains(address)) set._newAccounts.Add(address);
        }

        public void Storage(in StorageCell cell)
        {
            Account(cell.Address);
            if (add) set._storageKeys.Add(cell);
            else if (!set._storageKeys.Contains(cell)) set._newStorageKeys.Add(cell);
        }
    }
}
