// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.TxPool.Filters;

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

    /// <summary>The paymaster whose shared slots the pay-frame dependencies of <paramref name="tx"/> take, or
    /// <see langword="null"/> when they take per-transaction slots.</summary>
    /// <remarks>Only a paymaster under the pending limit (<see cref="FrameTxPaymasterFilter.IsNonCanonicalPaymaster"/>),
    /// as that limit is all that bounds the transactions sharing one of its slots.</remarks>
    public static Address? SlotPaymaster(Transaction tx, IReadOnlyStateProvider state) =>
        PendingPaymasterCache.KeyFor(tx) is Address paymaster && FrameTxPaymasterFilter.IsNonCanonicalPaymaster(paymaster, state) ? paymaster : null;

    /// <summary>Records <paramref name="dependencies"/> for <paramref name="txHash"/> unless that would put any account over <paramref name="cap"/>.</summary>
    /// <param name="paymaster">The paymaster from <see cref="SlotPaymaster"/>; without one, a pay-frame dependency takes a per-transaction slot.</param>
    /// <param name="replaced">The pending transaction <paramref name="txHash"/> replaces, whose slots count as free: it leaves the pool
    /// once the replacement is pooled.</param>
    /// <param name="recorded">Whether this call recorded the entry, and so owes its release if the transaction is not pooled.</param>
    /// <returns><see langword="false"/>, recording nothing, when an account is already at the cap.</returns>
    /// <remarks>A hash already recorded keeps its entry: a concurrent duplicate admission must not count twice.</remarks>
    public bool TryReserve(in ValueHash256 txHash, IReadOnlyList<FrameTxCodeDependency> dependencies, Address? paymaster, ValueHash256? replaced, int cap, out bool recorded)
    {
        recorded = false;
        if (dependencies.Count == 0) return true;

        lock (_lock)
        {
            if (_byTx.ContainsKey(txHash)) return true;

            // Looked up under the lock, so a replaced transaction a racing removal already released frees nothing twice.
            Entry released = replaced is { } replacedHash ? _byTx.GetValueOrDefault(replacedHash) : default;
            foreach (FrameTxCodeDependency dependency in dependencies)
            {
                if (!CanTakeSlot(dependency, paymaster, released, cap)) return false;
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
            Entry previous = _byTx.GetValueOrDefault(txHash);
            if (previous.Dependencies is null && dependencies.Count == 0) return true;

            foreach (FrameTxCodeDependency dependency in dependencies)
            {
                if (!CanTakeSlot(dependency, paymaster, previous, cap)) return false;
            }

            RemoveLocked(txHash);
            if (dependencies.Count > 0) AddLocked(txHash, dependencies, paymaster);
            added = previous.Dependencies is null;
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

    /// <summary>Whether <paramref name="dependency"/> can take its slot, counting as free the one <paramref name="released"/> holds for it.</summary>
    /// <remarks>A paymaster slot that already exists is shared, so it always passes; one <paramref name="released"/> holds
    /// frees a place in the paymaster set only when that entry is its last sharer.</remarks>
    private bool CanTakeSlot(in FrameTxCodeDependency dependency, Address? paymaster, in Entry released, int cap)
    {
        Address account = dependency.Account;
        if (!UsesPaymasterSlot(dependency, paymaster))
        {
            int freed = Holds(released, account, inPaymasterFrame: false) ? 1 : 0;
            return _pendingByAccount.GetValueOrDefault(account) - freed < cap;
        }

        if (_pendingByPaymasterSlot.ContainsKey((account, paymaster!))) return true;
        int freedSlots = Holds(released, account, inPaymasterFrame: true) && _pendingByPaymasterSlot[(account, released.Paymaster!)] == 1 ? 1 : 0;
        return _paymasterSlotsByAccount.GetValueOrDefault(account) - freedSlots < cap;
    }

    private static bool Holds(in Entry entry, Address account, bool inPaymasterFrame) =>
        entry.Dependencies is { } dependencies && ContainsSlot(dependencies, dependencies.Length, account, inPaymasterFrame);

    private void AddLocked(in ValueHash256 txHash, IReadOnlyList<FrameTxCodeDependency> dependencies, Address? paymaster)
    {
        FrameTxCodeDependency[] recorded = new FrameTxCodeDependency[dependencies.Count];
        for (int i = 0; i < recorded.Length; i++)
        {
            FrameTxCodeDependency dependency = dependencies[i];
            // Stored by the slot it takes, so the release and a later TryUpdate see the slot actually held.
            dependency = dependency with { InPaymasterFrame = UsesPaymasterSlot(dependency, paymaster) };
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
