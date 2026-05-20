// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text;
using Nethermind.Core.Collections;

namespace Nethermind.Core.BlockAccessLists;

/// <summary>
/// Block-level access list assembled by merging <see cref="BlockAccessListAtIndex"/> contributions
/// (one per transaction) into per-account, append-only collections. Ready for RLP encoding.
/// </summary>
/// <remarks>
/// Backed by a plain <see cref="Dictionary{TKey, TValue}"/> keyed by address; ordering is deferred
/// to the encoder rather than paid as O(log n) on every per-tx merge.
/// </remarks>
public class GeneratedBlockAccessList
{
    private static readonly Comparison<GeneratedAccountChanges> _byAddress = static (a, b) => a.Address.CompareTo(b.Address);

    private readonly Dictionary<Address, GeneratedAccountChanges> _accountChanges = new();

    public EnumerableWithCount<GeneratedAccountChanges> AccountChanges
        => new(_accountChanges.Values, _accountChanges.Values.Count);

    /// <summary>Address-sorted snapshot of the account-change values; pooled, dispose after use.</summary>
    public ArrayPoolListRef<GeneratedAccountChanges> GetSortedAccountChanges()
    {
        ArrayPoolListRef<GeneratedAccountChanges> result = new(_accountChanges.Count);
        foreach (GeneratedAccountChanges ac in _accountChanges.Values) result.Add(ac);
        result.AsSpan().Sort(_byAddress);
        return result;
    }

    public bool HasAccount(Address address) => _accountChanges.ContainsKey(address);

    public GeneratedAccountChanges? GetAccountChanges(Address address)
        => _accountChanges.TryGetValue(address, out GeneratedAccountChanges? value) ? value : null;

    public int ItemCount
    {
        get
        {
            int count = _accountChanges.Count;
            foreach (GeneratedAccountChanges acc in _accountChanges.Values)
            {
                count += acc.StorageChanges.Count + acc.StorageReads.Count;
            }
            return count;
        }
    }

    /// <summary>
    /// Merge a per-tx contribution into this block-level accumulator.
    /// </summary>
    public void Merge(BlockAccessListAtIndex other)
    {
        foreach (AccountChangesAtIndex sourceAccount in other.AccountChanges)
        {
            if (!_accountChanges.TryGetValue(sourceAccount.Address, out GeneratedAccountChanges? target))
            {
                target = new GeneratedAccountChanges(sourceAccount.Address);
                _accountChanges.Add(sourceAccount.Address, target);
            }
            target.Merge(sourceAccount);
        }
    }

    public void Clear() => _accountChanges.Clear();
    public void Reset() => Clear();

    /// <summary>
    /// For tests only — builds the accountChanges dictionary directly.
    /// </summary>
    internal static GeneratedBlockAccessList FromAccounts(IEnumerable<GeneratedAccountChanges> accounts)
    {
        GeneratedBlockAccessList bal = new();
        foreach (GeneratedAccountChanges a in accounts)
        {
            bal._accountChanges.Add(a.Address, a);
        }
        return bal;
    }

    public override string ToString()
    {
        StringBuilder sb = new();
        sb.AppendLine($"GeneratedBlockAccessList (Accounts={_accountChanges.Count})");
        using ArrayPoolListRef<GeneratedAccountChanges> sorted = GetSortedAccountChanges();
        foreach (GeneratedAccountChanges ac in sorted)
        {
            sb.Append("  ").AppendLine(ac.ToString());
        }
        return sb.ToString();
    }
}
