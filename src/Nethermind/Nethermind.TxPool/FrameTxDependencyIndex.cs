// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.TxPool;

/// <summary>
/// Maps the chain-head accounts a pending EIP-8141 frame transaction's validation prefix depends on
/// back to that transaction, so a new head only revalidates the prefixes it could have invalidated.
/// </summary>
/// <remarks>
/// EIP-8141 "Revalidation" asks nodes to index pending transactions by their dependency set. Without
/// the index the only correct alternative is re-simulating the whole pool on every head, which is
/// itself a denial-of-service vector. Account granularity is a conservative superset of the spec's
/// slot granularity: a write to a sender storage slot also changes that sender's account.
/// </remarks>
internal sealed class FrameTxDependencyIndex
{
    private readonly ConcurrentDictionary<ValueHash256, AddressAsKey[]> _byTx = new();
    private readonly ConcurrentDictionary<AddressAsKey, ConcurrentDictionary<ValueHash256, bool>> _byAccount = new();

    public int Count => _byTx.Count;

    /// <summary>Indexes <paramref name="hash"/> under each of <paramref name="accounts"/>, replacing any earlier entry.</summary>
    public void Set(ValueHash256 hash, AddressAsKey[] accounts)
    {
        Remove(hash);
        if (accounts.Length == 0) return;

        _byTx[hash] = accounts;
        foreach (AddressAsKey account in accounts)
        {
            _byAccount.GetOrAdd(account, static _ => new ConcurrentDictionary<ValueHash256, bool>())[hash] = true;
        }
    }

    public void Remove(ValueHash256 hash)
    {
        if (!_byTx.TryRemove(hash, out AddressAsKey[]? accounts)) return;

        foreach (AddressAsKey account in accounts)
        {
            if (_byAccount.TryGetValue(account, out ConcurrentDictionary<ValueHash256, bool>? hashes))
            {
                hashes.TryRemove(hash, out _);
                // Racy by design: a concurrent Set may re-add to a bucket about to be dropped, so the
                // removal is conditional on the bucket still being the empty one observed here.
                if (hashes.IsEmpty)
                {
                    ((ICollection<KeyValuePair<AddressAsKey, ConcurrentDictionary<ValueHash256, bool>>>)_byAccount)
                        .Remove(new KeyValuePair<AddressAsKey, ConcurrentDictionary<ValueHash256, bool>>(account, hashes));
                }
            }
        }
    }

    /// <summary>Adds to <paramref name="into"/> every indexed transaction depending on a changed account.</summary>
    public void CollectAffected(IReadOnlyList<AddressAsKey> changedAccounts, HashSet<ValueHash256> into)
    {
        for (int i = 0; i < changedAccounts.Count; i++)
        {
            if (_byAccount.TryGetValue(changedAccounts[i], out ConcurrentDictionary<ValueHash256, bool>? hashes))
            {
                foreach (KeyValuePair<ValueHash256, bool> entry in hashes) into.Add(entry.Key);
            }
        }
    }

    /// <summary>Adds every indexed transaction, for heads that report no per-account change set.</summary>
    public void CollectAll(HashSet<ValueHash256> into)
    {
        foreach (KeyValuePair<ValueHash256, AddressAsKey[]> entry in _byTx) into.Add(entry.Key);
    }
}
