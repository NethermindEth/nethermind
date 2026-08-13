// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.TxPool;

/// <summary>
/// Maps the chain-head accounts a pending EIP-8141 frame transaction's validation prefix depends on
/// back to that transaction, so a new head only revalidates the prefixes it could have invalidated.
/// </summary>
/// <remarks>Account granularity is a conservative superset of the spec's slot granularity. A missed entry
/// is a missed revalidation, so mutation and collection are serialized rather than reasoned about lock-free.</remarks>
internal sealed class FrameTxDependencyIndex
{
    private readonly Lock _lock = new();
    private readonly Dictionary<ValueHash256, AddressAsKey[]> _byTx = [];
    private readonly Dictionary<AddressAsKey, HashSet<ValueHash256>> _byAccount = [];

    public int Count
    {
        get { lock (_lock) return _byTx.Count; }
    }

    /// <summary>Indexes <paramref name="hash"/> under each of <paramref name="accounts"/>, replacing any earlier entry.</summary>
    public void Set(ValueHash256 hash, AddressAsKey[] accounts)
    {
        lock (_lock)
        {
            RemoveLocked(hash);
            if (accounts.Length == 0) return;

            _byTx[hash] = accounts;
            foreach (AddressAsKey account in accounts)
            {
                if (!_byAccount.TryGetValue(account, out HashSet<ValueHash256>? hashes))
                {
                    _byAccount[account] = hashes = [];
                }

                hashes.Add(hash);
            }
        }
    }

    public void Remove(ValueHash256 hash)
    {
        lock (_lock) RemoveLocked(hash);
    }

    /// <summary>Adds to <paramref name="into"/> every indexed transaction depending on a changed account.</summary>
    public void CollectAffected(IReadOnlyList<AddressAsKey> changedAccounts, HashSet<ValueHash256> into)
    {
        lock (_lock)
        {
            for (int i = 0; i < changedAccounts.Count; i++)
            {
                if (_byAccount.TryGetValue(changedAccounts[i], out HashSet<ValueHash256>? hashes)) into.UnionWith(hashes);
            }
        }
    }

    /// <summary>Adds every indexed transaction, for heads whose change list does not describe everything that moved.</summary>
    public void CollectAll(HashSet<ValueHash256> into)
    {
        lock (_lock) into.UnionWith(_byTx.Keys);
    }

    private void RemoveLocked(ValueHash256 hash)
    {
        if (!_byTx.Remove(hash, out AddressAsKey[]? accounts)) return;

        foreach (AddressAsKey account in accounts)
        {
            if (_byAccount.TryGetValue(account, out HashSet<ValueHash256>? hashes) && hashes.Remove(hash) && hashes.Count == 0)
            {
                _byAccount.Remove(account);
            }
        }
    }
}
