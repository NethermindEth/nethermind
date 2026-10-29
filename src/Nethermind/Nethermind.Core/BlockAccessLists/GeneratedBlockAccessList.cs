// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text;
using Nethermind.Core.Collections;

namespace Nethermind.Core.BlockAccessLists;

/// <summary>
/// Block-level access list built from a <see cref="MergedBlockAccessList"/> for consumers that keep it after the block.
/// Ready for RLP encoding.
/// </summary>
/// <remarks>
/// Address ordering is deferred to the encoder boundary.
/// </remarks>
public class GeneratedBlockAccessList
{
    private readonly Dictionary<AddressAsKey, GeneratedAccountChanges> _accountChanges = new(GenericEqualityComparer.GetOptimized<AddressAsKey>());

    /// <summary>
    /// Insertion-ordered view over the BAL's accounts.
    /// struct enumerator; <c>.Count</c> exposes the underlying dictionary size.
    /// </summary>
    public GeneratedAccountChangesView AccountChanges => new(_accountChanges);

    /// <summary>
    /// Address-sorted snapshot; pooled, dispose after use.
    /// </summary>
    public ArrayPoolListRef<GeneratedAccountChanges> GetSortedAccountChanges()
    {
        ArrayPoolListRef<GeneratedAccountChanges> result = new(_accountChanges.Count);
        foreach (GeneratedAccountChanges ac in _accountChanges.Values) result.Add(ac);
        result.AsSpan().Sort(GenericComparer.GetOptimized<GeneratedAccountChanges>());
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

    /// <summary>Builds the list from complete accounts.</summary>
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
