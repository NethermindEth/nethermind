// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.Tracing;

namespace Nethermind.TxPool;

/// <summary>
/// Tracks the code dependencies of pending EIP-8141 frame transactions: accounts their validation prefix relied on
/// whose code could change, that is EIP-8298 targets reachable through DELEGATECALL or CALLCODE, and accounts EIP-8151
/// ecRecover returned.
/// </summary>
/// <remarks>Keyed by transaction hash, so release is idempotent and cannot drift from what was reserved.
/// The per-account count bounds how many pending transactions one code change can invalidate at once: SETCODEFROM
/// rewrites only the account it executes as, and revalidation is triggered per account, so accounts sharing a code
/// hash do not share a budget.
/// A dependency recorded only in the pay frame of a paymaster (<see cref="FrameTxCodeDependency.InPaymasterFrame"/>)
/// takes one slot per paymaster instead, in a second set of slots per account under the same cap: the paymaster's
/// own pending limit bounds how many transactions share that slot, so transactions relying on the account in their
/// verify frames cannot fill the slots the paymaster needs, while the account still sits in at most cap paymasters'
/// pay frames. That second set can still be filled, by cap distinct paymasters relying on the account.</remarks>
internal sealed class PendingCodeDependencyCache
{
    private readonly Lock _lock = new();
    private readonly Dictionary<ValueHash256, Entry> _byTx = [];
    private readonly Dictionary<AddressAsKey, int> _pendingByAccount = [];
    private readonly Dictionary<(AddressAsKey Account, AddressAsKey Paymaster), int> _pendingByPaymasterSlot = [];
    private readonly Dictionary<AddressAsKey, int> _paymasterSlotsByAccount = [];

    /// <summary>Pending transactions currently relying on the code of <paramref name="account"/> outside a paymaster's pay frame.</summary>
    public int GetPendingCount(Address account)
    {
        lock (_lock) return _pendingByAccount.TryGetValue(account, out int count) ? count : 0;
    }

    /// <summary>Records <paramref name="dependencies"/> for <paramref name="txHash"/> unless that would put any account over <paramref name="cap"/>.</summary>
    /// <param name="paymaster">The paymaster the transaction pays through (<see cref="PendingPaymasterCache.KeyFor"/>);
    /// without one, a pay-frame dependency takes a per-transaction slot.</param>
    /// <param name="recorded">Whether this call recorded the entry, and so owes its release if the transaction is not pooled.</param>
    /// <returns><see langword="false"/>, recording nothing, when an account is already at the cap.</returns>
    /// <remarks>A hash already recorded keeps its entry: a concurrent duplicate admission must not count twice.</remarks>
    public bool TryReserve(in ValueHash256 txHash, IReadOnlyList<FrameTxCodeDependency> dependencies, Address? paymaster, int cap, out bool recorded)
    {
        recorded = false;
        if (dependencies.Count == 0) return true;

        lock (_lock)
        {
            if (_byTx.ContainsKey(txHash)) return true;

            foreach (FrameTxCodeDependency dependency in dependencies)
            {
                if (UsesPaymasterSlot(dependency, paymaster)
                    ? !HasPaymasterSlot(dependency.Account, paymaster!, cap)
                    : _pendingByAccount.GetValueOrDefault(dependency.Account) >= cap) return false;
            }

            AddLocked(txHash, dependencies, paymaster);
            recorded = true;
            return true;
        }
    }

    /// <summary>Replaces the dependencies of a pooled transaction with those revalidation re-simulated.</summary>
    /// <param name="paymaster">As for <see cref="TryReserve"/>.</param>
    /// <param name="added">Whether a transaction untracked until now gained an entry, which the caller must release
    /// if an eviction racing revalidation has already left it unpooled.</param>
    /// <returns><see langword="false"/>, leaving the old entry for the eviction to release, when a new account is over <paramref name="cap"/>.</returns>
    public bool TryUpdate(in ValueHash256 txHash, IReadOnlyList<FrameTxCodeDependency> dependencies, Address? paymaster, int cap, out bool added)
    {
        added = false;
        lock (_lock)
        {
            FrameTxCodeDependency[] previous = _byTx.GetValueOrDefault(txHash).Dependencies ?? [];
            if (previous.Length == 0 && dependencies.Count == 0) return true;

            foreach (FrameTxCodeDependency dependency in dependencies)
            {
                // A paymaster slot this transaction holds already exists, so it passes as one shared with others.
                if (UsesPaymasterSlot(dependency, paymaster))
                {
                    if (!HasPaymasterSlot(dependency.Account, paymaster!, cap)) return false;
                    continue;
                }

                int held = ContainsSlot(previous, previous.Length, dependency.Account, inPaymasterFrame: false) ? 1 : 0;
                if (_pendingByAccount.GetValueOrDefault(dependency.Account) - held >= cap) return false;
            }

            RemoveLocked(txHash);
            if (dependencies.Count > 0) AddLocked(txHash, dependencies, paymaster);
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
        lock (_lock) return _byTx.TryGetValue(txHash, out Entry entry) ? entry.Dependencies : [];
    }

    private static bool UsesPaymasterSlot(in FrameTxCodeDependency dependency, Address? paymaster) =>
        dependency.InPaymasterFrame && paymaster is not null;

    private bool HasPaymasterSlot(Address account, Address paymaster, int cap) =>
        _pendingByPaymasterSlot.ContainsKey((account, paymaster)) || _paymasterSlotsByAccount.GetValueOrDefault(account) < cap;

    private void AddLocked(in ValueHash256 txHash, IReadOnlyList<FrameTxCodeDependency> dependencies, Address? paymaster)
    {
        FrameTxCodeDependency[] recorded = new FrameTxCodeDependency[dependencies.Count];
        for (int i = 0; i < recorded.Length; i++)
        {
            FrameTxCodeDependency dependency = dependencies[i];
            // Stored by the slot it takes, so the release and a later TryUpdate see the slot actually held.
            if (dependency.InPaymasterFrame && paymaster is null) dependency = dependency with { InPaymasterFrame = false };
            recorded[i] = dependency;
            // Counted once per transaction, should an account be listed twice.
            if (ContainsSlot(recorded, i, dependency.Account, dependency.InPaymasterFrame)) continue;
            if (dependency.InPaymasterFrame)
            {
                (AddressAsKey, AddressAsKey) slot = (dependency.Account, paymaster!);
                int sharing = _pendingByPaymasterSlot.GetValueOrDefault(slot);
                if (sharing == 0) _paymasterSlotsByAccount[dependency.Account] = _paymasterSlotsByAccount.GetValueOrDefault(dependency.Account) + 1;
                _pendingByPaymasterSlot[slot] = sharing + 1;
            }
            else
            {
                _pendingByAccount[dependency.Account] = _pendingByAccount.GetValueOrDefault(dependency.Account) + 1;
            }
        }

        _byTx[txHash] = new Entry(recorded, paymaster);
    }

    private static bool ContainsSlot(FrameTxCodeDependency[] dependencies, int count, Address account, bool inPaymasterFrame)
    {
        for (int i = 0; i < count; i++)
        {
            if (dependencies[i].Account == account && dependencies[i].InPaymasterFrame == inPaymasterFrame) return true;
        }

        return false;
    }

    private void RemoveLocked(in ValueHash256 txHash)
    {
        if (!_byTx.Remove(txHash, out Entry entry)) return;

        FrameTxCodeDependency[] dependencies = entry.Dependencies;
        for (int i = 0; i < dependencies.Length; i++)
        {
            FrameTxCodeDependency dependency = dependencies[i];
            if (ContainsSlot(dependencies, i, dependency.Account, dependency.InPaymasterFrame)) continue;
            if (dependency.InPaymasterFrame)
            {
                (AddressAsKey, AddressAsKey) slot = (dependency.Account, entry.Paymaster!);
                if (Decrement(_pendingByPaymasterSlot, slot)) Decrement(_paymasterSlotsByAccount, dependency.Account);
            }
            else
            {
                Decrement(_pendingByAccount, dependency.Account);
            }
        }
    }

    /// <returns>Whether the count reached zero and its key was removed.</returns>
    private static bool Decrement<TKey>(Dictionary<TKey, int> counts, TKey key) where TKey : notnull
    {
        int remaining = counts[key] - 1;
        if (remaining > 0)
        {
            counts[key] = remaining;
            return false;
        }

        counts.Remove(key);
        return true;
    }

    /// <param name="Paymaster">The paymaster whose slots the pay-frame dependencies hold.</param>
    private readonly record struct Entry(FrameTxCodeDependency[] Dependencies, Address? Paymaster);
}
