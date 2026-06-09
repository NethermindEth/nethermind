// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Serialization;
using Nethermind.Core.Crypto;

namespace Nethermind.Core.BlockAccessLists;

/// <summary>
/// Block-level access list as decoded from the network or storage. Optimised for reading:
/// account lookup is O(1) via hash map. Iteration order matches insertion order — the decoder
/// inserts accounts in the order they arrive on the wire (which it has already validated as
/// sorted by address), so enumerating <see cref="AccountChanges"/> walks accounts in sorted
/// address order. The only mutation permitted is the prestate load.
/// </summary>
public class ReadOnlyBlockAccessList : IEquatable<ReadOnlyBlockAccessList>
{
    private readonly Dictionary<AddressAsKey, ReadOnlyAccountChanges> _accountChanges;
    private readonly ReadOnlyAccountChanges[] _orderedAccounts;

    [JsonIgnore]
    public int ItemCount { get; }

    /// <summary>
    /// Sum of <see cref="ReadOnlyAccountChanges.StorageReads"/> lengths across all accounts.
    /// Cached once at construction so per-block validation doesn't re-walk the BAL.
    /// </summary>
    [JsonIgnore]
    public int TotalStorageReads { get; }

    /// <summary>
    /// Sum of per-slot change-event counts (<c>StorageChanges[i].Changes.Length</c>) across all
    /// accounts. Bounds the total (slot, tx) pairs the generator can produce in a valid block.
    /// </summary>
    [JsonIgnore]
    public int TotalStorageChangeEvents { get; }

    /// <summary>
    /// Keccak of the BAL's wire (RLP) encoding, cached by the decoder so the consensus-side hash
    /// check avoids re-hashing per block. <c>null</c> for BALs synthesised in-process.
    /// </summary>
    [JsonIgnore]
    public Hash256? WireHash { get; }

    /// <summary>
    /// Address-sorted view over the BAL's accounts. <c>foreach</c> walks the underlying array
    /// via <see cref="ReadOnlySpan{T}"/> with no enumerator allocation; <see cref="ReadOnlyAccountChangesView.AsSpan"/>
    /// exposes the raw span for span-only call sites.
    /// </summary>
    public ReadOnlyAccountChangesView AccountChanges => new(_orderedAccounts);

    public bool HasAccount(Address address) => _accountChanges.ContainsKey(address);

    public ReadOnlyAccountChanges? GetAccountChanges(Address address)
        => _accountChanges.TryGetValue(address, out ReadOnlyAccountChanges? value) ? value : null;

    public ReadOnlyBlockAccessList() : this([], 0) { }

    /// <summary>
    /// Constructs a read-only BAL from accounts already in sorted address order (as guaranteed
    /// by the RLP decoder). The dictionary preserves insertion order during iteration provided
    /// no entries are removed — and this type is immutable post-construction except for prestate
    /// loading, which only mutates per-account fields, so the sorted iteration is preserved.
    /// </summary>
    public ReadOnlyBlockAccessList(ReadOnlyAccountChanges[] orderedAccounts, int itemCount)
        : this(orderedAccounts, itemCount, wireHash: null) { }

    public ReadOnlyBlockAccessList(ReadOnlyAccountChanges[] orderedAccounts, int itemCount, Hash256? wireHash)
    {
        _orderedAccounts = orderedAccounts;
        _accountChanges = new Dictionary<AddressAsKey, ReadOnlyAccountChanges>(orderedAccounts.Length);
        int totalReads = 0;
        int totalChangeEvents = 0;
        foreach (ReadOnlyAccountChanges a in orderedAccounts)
        {
            _accountChanges.Add(a.Address, a);
            totalReads += a.StorageReads.Length;
            foreach (ReadOnlySlotChanges slot in a.StorageChanges) totalChangeEvents += slot.Changes.Length;
        }
        ItemCount = itemCount;
        TotalStorageReads = totalReads;
        TotalStorageChangeEvents = totalChangeEvents;
        WireHash = wireHash;
    }

    public bool Equals(ReadOnlyBlockAccessList? other)
    {
        if (other is null) return false;
        if (_accountChanges.Count != other._accountChanges.Count) return false;
        foreach (KeyValuePair<AddressAsKey, ReadOnlyAccountChanges> kv in _accountChanges)
        {
            if (!other._accountChanges.TryGetValue(kv.Key, out ReadOnlyAccountChanges? otherAcc)) return false;
            if (!kv.Value.Equals(otherAcc)) return false;
        }
        return true;
    }

    public override bool Equals(object? obj) => obj is ReadOnlyBlockAccessList other && Equals(other);

    public override int GetHashCode() => _accountChanges.Count.GetHashCode();

    public override string ToString()
    {
        StringBuilder sb = new();
        sb.AppendLine($"ReadOnlyBlockAccessList (Accounts={_accountChanges.Count})");
        foreach (ReadOnlyAccountChanges ac in _accountChanges.Values)
        {
            sb.Append("  ").AppendLine(ac.ToString());
        }
        return sb.ToString();
    }
}
