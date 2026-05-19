// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Serialization;
using Nethermind.Core.Collections;

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
    private readonly Dictionary<Address, ReadOnlyAccountChanges> _accountChanges;

    [JsonIgnore]
    public int ItemCount { get; }

    public EnumerableWithCount<ReadOnlyAccountChanges> AccountChanges
        => new(_accountChanges.Values, _accountChanges.Count);

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
    {
        _accountChanges = new Dictionary<Address, ReadOnlyAccountChanges>(orderedAccounts.Length);
        foreach (ReadOnlyAccountChanges a in orderedAccounts)
        {
            _accountChanges.Add(a.Address, a);
        }
        ItemCount = itemCount;
    }

    public bool Equals(ReadOnlyBlockAccessList? other)
    {
        if (other is null) return false;
        if (_accountChanges.Count != other._accountChanges.Count) return false;
        foreach (KeyValuePair<Address, ReadOnlyAccountChanges> kv in _accountChanges)
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
