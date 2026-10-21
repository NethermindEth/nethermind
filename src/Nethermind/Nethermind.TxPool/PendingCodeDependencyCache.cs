// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.Tracing;

namespace Nethermind.TxPool;

/// <summary>
/// Tracks the EIP-8298 code dependencies of pending EIP-8141 frame transactions: targets their validation
/// prefix relied on whose code could change through DELEGATECALL or CALLCODE.
/// </summary>
/// <remarks>Keyed by transaction hash, so release is idempotent and cannot drift from what was reserved.
/// The per-account count bounds how many pending transactions one code change can invalidate at once: SETCODEFROM
/// rewrites only the account it executes as, and revalidation is triggered per account, so accounts sharing a code
/// hash do not share a budget.</remarks>
internal sealed class PendingCodeDependencyCache
{
    private readonly Lock _lock = new();
    private readonly Dictionary<ValueHash256, FrameTxCodeDependency[]> _byTx = [];
    private readonly Dictionary<AddressAsKey, int> _pendingByAccount = [];

    /// <summary>Pending transactions currently relying on the code of <paramref name="account"/>.</summary>
    public int GetPendingCount(Address account)
    {
        lock (_lock) return _pendingByAccount.TryGetValue(account, out int count) ? count : 0;
    }

    /// <summary>Records <paramref name="dependencies"/> for <paramref name="txHash"/> unless that would put any account over <paramref name="cap"/>.</summary>
    /// <param name="recorded">Whether this call recorded the entry, and so owes its release if the transaction is not pooled.</param>
    /// <returns><see langword="false"/>, recording nothing, when an account is already at the cap.</returns>
    /// <remarks>A hash already recorded keeps its entry: a concurrent duplicate admission must not count twice.</remarks>
    public bool TryReserve(in ValueHash256 txHash, IReadOnlyList<FrameTxCodeDependency> dependencies, int cap, out bool recorded)
    {
        recorded = false;
        if (dependencies.Count == 0) return true;

        lock (_lock)
        {
            if (_byTx.ContainsKey(txHash)) return true;

            foreach (FrameTxCodeDependency dependency in dependencies)
            {
                if (_pendingByAccount.GetValueOrDefault(dependency.Account) >= cap) return false;
            }

            AddLocked(txHash, dependencies);
            recorded = true;
            return true;
        }
    }

    /// <summary>Replaces the dependencies of a pooled transaction with those revalidation re-simulated.</summary>
    /// <param name="added">Whether a transaction untracked until now gained an entry, which the caller must release
    /// if an eviction racing revalidation has already left it unpooled.</param>
    /// <returns><see langword="false"/>, leaving the old entry for the eviction to release, when a new account is over <paramref name="cap"/>.</returns>
    public bool TryUpdate(in ValueHash256 txHash, IReadOnlyList<FrameTxCodeDependency> dependencies, int cap, out bool added)
    {
        added = false;
        lock (_lock)
        {
            FrameTxCodeDependency[] previous = _byTx.GetValueOrDefault(txHash) ?? [];
            if (previous.Length == 0 && dependencies.Count == 0) return true;

            foreach (FrameTxCodeDependency dependency in dependencies)
            {
                int held = ContainsAccount(previous, previous.Length, dependency.Account) ? 1 : 0;
                if (_pendingByAccount.GetValueOrDefault(dependency.Account) - held >= cap) return false;
            }

            RemoveLocked(txHash);
            if (dependencies.Count > 0) AddLocked(txHash, dependencies);
            added = previous.Length == 0;
            return true;
        }
    }

    /// <summary>Releases whatever <paramref name="txHash"/> reserved; a no-op when it reserved nothing.</summary>
    public void Release(in ValueHash256 txHash)
    {
        lock (_lock) RemoveLocked(txHash);
    }

    /// <summary>The accounts <paramref name="txHash"/> depends on, or empty when it reserved nothing.</summary>
    public FrameTxCodeDependency[] Get(in ValueHash256 txHash)
    {
        lock (_lock) return _byTx.TryGetValue(txHash, out FrameTxCodeDependency[]? dependencies) ? dependencies : [];
    }

    private void AddLocked(in ValueHash256 txHash, IReadOnlyList<FrameTxCodeDependency> dependencies)
    {
        FrameTxCodeDependency[] recorded = new FrameTxCodeDependency[dependencies.Count];
        for (int i = 0; i < recorded.Length; i++)
        {
            recorded[i] = dependencies[i];
            // Counted once per transaction, should an account be listed twice.
            if (ContainsAccount(recorded, i, recorded[i].Account)) continue;
            _pendingByAccount[recorded[i].Account] = _pendingByAccount.GetValueOrDefault(recorded[i].Account) + 1;
        }

        _byTx[txHash] = recorded;
    }

    private static bool ContainsAccount(FrameTxCodeDependency[] dependencies, int count, Address account)
    {
        for (int i = 0; i < count; i++)
        {
            if (dependencies[i].Account == account) return true;
        }

        return false;
    }

    private void RemoveLocked(in ValueHash256 txHash)
    {
        if (!_byTx.Remove(txHash, out FrameTxCodeDependency[]? dependencies)) return;

        for (int i = 0; i < dependencies.Length; i++)
        {
            FrameTxCodeDependency dependency = dependencies[i];
            if (ContainsAccount(dependencies, i, dependency.Account)) continue;
            int remaining = _pendingByAccount[dependency.Account] - 1;
            if (remaining == 0) _pendingByAccount.Remove(dependency.Account);
            else _pendingByAccount[dependency.Account] = remaining;
        }
    }
}
