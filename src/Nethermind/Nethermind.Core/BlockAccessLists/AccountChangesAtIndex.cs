// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Int256;

namespace Nethermind.Core.BlockAccessLists;

/// <summary>
/// Per-account changes recorded at a single BAL index (i.e. one transaction). Each scalar field
/// (balance / nonce / code) collapses to a single optional value because at one index there can
/// only be one final value. Storage changes carry one <see cref="StorageChange"/> per slot.
/// </summary>
public class AccountChangesAtIndex(Address address)
{
    public Address Address { get; } = address;

    public BalanceChange? BalanceChange { get; set; }
    public NonceChange? NonceChange { get; set; }
    public CodeChange? CodeChange { get; set; }

    private readonly SortedDictionary<UInt256, StorageChange> _storageChanges
        = new(GenericComparer.GetOptimized<UInt256>());
    private readonly SortedSet<UInt256> _storageReads
        = new(GenericComparer.GetOptimized<UInt256>());

    public IReadOnlyCollection<UInt256> ChangedSlots => _storageChanges.Keys;
    public IEnumerable<KeyValuePair<UInt256, StorageChange>> StorageChanges => _storageChanges;
    public int StorageChangeCount => _storageChanges.Count;
    public IReadOnlyCollection<UInt256> StorageReads => _storageReads;

    public bool HasStorageChange(UInt256 key) => _storageChanges.ContainsKey(key);

    public bool TryGetStorageChange(UInt256 key, [NotNullWhen(true)] out StorageChange? storageChange)
    {
        if (_storageChanges.TryGetValue(key, out StorageChange existing))
        {
            storageChange = existing;
            return true;
        }
        storageChange = null;
        return false;
    }

    public void SetStorageChange(UInt256 key, StorageChange storageChange)
        => _storageChanges[key] = storageChange;

    public bool RemoveStorageChange(UInt256 key) => _storageChanges.Remove(key);

    public void AddStorageRead(UInt256 key) => _storageReads.Add(key);

    public bool RemoveStorageRead(UInt256 key) => _storageReads.Remove(key);

    public void ClearStorage()
    {
        _storageChanges.Clear();
        _storageReads.Clear();
    }
}
