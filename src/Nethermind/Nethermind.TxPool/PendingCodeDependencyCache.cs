// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using Nethermind.Core.Crypto;
using Nethermind.Evm.Tracing;

namespace Nethermind.TxPool;

/// <summary>
/// Tracks the EIP-8298 code dependencies of pending EIP-8141 frame transactions: targets their validation
/// prefix relied on whose code could change through DELEGATECALL or CALLCODE.
/// </summary>
/// <remarks>Keyed by transaction hash, so release is idempotent and cannot drift from what was reserved.
/// The per-code-hash count bounds how many pending transactions one code change can invalidate at once.</remarks>
internal sealed class PendingCodeDependencyCache
{
    private readonly Lock _lock = new();
    private readonly Dictionary<ValueHash256, FrameTxCodeDependency[]> _byTx = [];
    private readonly Dictionary<ValueHash256, int> _pendingByCodeHash = [];

    /// <summary>Pending transactions currently relying on code with <paramref name="codeHash"/>.</summary>
    public int GetPendingCount(in ValueHash256 codeHash)
    {
        lock (_lock) return _pendingByCodeHash.TryGetValue(codeHash, out int count) ? count : 0;
    }

    /// <summary>Records <paramref name="dependencies"/> for <paramref name="txHash"/> unless that would put any code hash over <paramref name="cap"/>.</summary>
    /// <param name="recorded">Whether this call recorded the entry, and so owes its release if the transaction is not pooled.</param>
    /// <returns><see langword="false"/>, recording nothing, when a code hash is already at the cap.</returns>
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
                if (_pendingByCodeHash.GetValueOrDefault(dependency.CodeHash) >= cap) return false;
            }

            AddLocked(txHash, dependencies);
            recorded = true;
            return true;
        }
    }

    /// <summary>Replaces the dependencies of a pooled transaction with those revalidation re-simulated.</summary>
    /// <param name="added">Whether a transaction untracked until now gained an entry, which the caller must release
    /// if an eviction racing revalidation has already left it unpooled.</param>
    /// <returns><see langword="false"/>, leaving the old entry for the eviction to release, when a new code hash is over <paramref name="cap"/>.</returns>
    public bool TryUpdate(in ValueHash256 txHash, IReadOnlyList<FrameTxCodeDependency> dependencies, int cap, out bool added)
    {
        added = false;
        lock (_lock)
        {
            FrameTxCodeDependency[] previous = _byTx.GetValueOrDefault(txHash) ?? [];
            if (previous.Length == 0 && dependencies.Count == 0) return true;

            foreach (FrameTxCodeDependency dependency in dependencies)
            {
                int held = ContainsCodeHash(previous, previous.Length, dependency.CodeHash) ? 1 : 0;
                if (_pendingByCodeHash.GetValueOrDefault(dependency.CodeHash) - held >= cap) return false;
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
            // Counted once per transaction: two targets sharing code are one dependency for the cap.
            if (ContainsCodeHash(recorded, i, recorded[i].CodeHash)) continue;
            _pendingByCodeHash[recorded[i].CodeHash] = _pendingByCodeHash.GetValueOrDefault(recorded[i].CodeHash) + 1;
        }

        _byTx[txHash] = recorded;
    }

    private static bool ContainsCodeHash(FrameTxCodeDependency[] dependencies, int count, in ValueHash256 codeHash)
    {
        for (int i = 0; i < count; i++)
        {
            if (dependencies[i].CodeHash == codeHash) return true;
        }

        return false;
    }

    private void RemoveLocked(in ValueHash256 txHash)
    {
        if (!_byTx.Remove(txHash, out FrameTxCodeDependency[]? dependencies)) return;

        for (int i = 0; i < dependencies.Length; i++)
        {
            FrameTxCodeDependency dependency = dependencies[i];
            if (ContainsCodeHash(dependencies, i, dependency.CodeHash)) continue;
            int remaining = _pendingByCodeHash[dependency.CodeHash] - 1;
            if (remaining == 0) _pendingByCodeHash.Remove(dependency.CodeHash);
            else _pendingByCodeHash[dependency.CodeHash] = remaining;
        }
    }
}
