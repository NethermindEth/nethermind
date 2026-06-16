// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Int256;

namespace Nethermind.State.Flat;

/// <summary>
/// Persistence-read cache shared ACROSS heads, replacing the per-bundle memo for at-head
/// serving: without it, every new head starts an empty memo and the first eth_calls re-read
/// the serving working set (~10k slots) from the database — the measured per-head refault.
/// </summary>
/// <remarks>
/// Correctness rests on three facts, in order:
/// <list type="bullet">
/// <item>Reads consult the in-memory snapshot layer (recent block diffs) BEFORE this cache,
/// so a slot written by an unpersisted block never reaches a stale entry.</item>
/// <item>When a block persists, <see cref="OnSnapshotPersisted"/> removes exactly its
/// write-set from the cache BEFORE the snapshot leaves the in-memory layer — so an entry
/// that survives was not written by any persisted block since it was cached.</item>
/// <item>A bundle may attach this cache only when its persistence reader's basis equals
/// <see cref="_basis"/> at creation (<see cref="BasisMatches"/>): bundles serving older
/// heads keep their private memo, which excludes the one remaining stale scenario (a slot
/// rewritten and persisted past that bundle's head).</item>
/// </list>
/// Self-destructed storage cannot be invalidated per-key (its slot set is unknown), so a
/// persisted self-destruct clears the cache — rare after EIP-6780.
/// </remarks>
public sealed class CarryForwardReadCache(int maxEntriesPerKind, StateId initialBasis)
{
    private readonly ConcurrentDictionary<HashedKey<(Address, UInt256)>, byte[]?> _slots = new();
    private readonly ConcurrentDictionary<HashedKey<Address>, Account?> _accounts = new();
    private readonly int _maxEntriesPerKind = maxEntriesPerKind;
    private int _slotCount;
    private int _accountCount;

    private readonly Lock _basisLock = new();
    private StateId _basis = initialBasis;

    /// <summary>Whether a bundle whose persistence reader sees <paramref name="readerState"/>
    /// may serve from and fill this cache.</summary>
    public bool BasisMatches(in StateId readerState)
    {
        lock (_basisLock)
        {
            return readerState == _basis;
        }
    }

    public bool TryGetSlot(HashedKey<(Address, UInt256)> key, out byte[]? value) => _slots.TryGetValue(key, out value);

    public void AddSlot(HashedKey<(Address, UInt256)> key, byte[]? value)
    {
        if (Volatile.Read(ref _slotCount) >= _maxEntriesPerKind)
        {
            // Self-healing eviction: refilling costs one head's worth of refaults.
            _slots.Clear();
            Interlocked.Exchange(ref _slotCount, 0);
        }

        if (_slots.TryAdd(key, value))
        {
            Interlocked.Increment(ref _slotCount);
        }
    }

    public bool TryGetAccount(HashedKey<Address> key, out Account? account) => _accounts.TryGetValue(key, out account);

    public void AddAccount(HashedKey<Address> key, Account? account)
    {
        if (Volatile.Read(ref _accountCount) >= _maxEntriesPerKind)
        {
            _accounts.Clear();
            Interlocked.Exchange(ref _accountCount, 0);
        }

        if (_accounts.TryAdd(key, account))
        {
            Interlocked.Increment(ref _accountCount);
        }
    }

    /// <summary>
    /// Invalidates the persisted block's write-set and advances the basis. Runs under the
    /// persistence lock, after the database commit and while the snapshot is still present
    /// in the in-memory layer — bundles created concurrently still shadow this cache through
    /// their leased snapshot list.
    /// </summary>
    public void OnSnapshotPersisted(Snapshot snapshot)
    {
        if (snapshot.HasSelfDestructedStorageAddresses)
        {
            _slots.Clear();
            Interlocked.Exchange(ref _slotCount, 0);
            _accounts.Clear();
            Interlocked.Exchange(ref _accountCount, 0);
        }
        else
        {
            foreach (KeyValuePair<HashedKey<(Address, UInt256)>, SlotValue?> written in snapshot.Storages)
            {
                if (_slots.TryRemove(written.Key, out _))
                {
                    Interlocked.Decrement(ref _slotCount);
                }
            }

            foreach (KeyValuePair<HashedKey<Address>, Account?> written in snapshot.Accounts)
            {
                if (_accounts.TryRemove(written.Key, out _))
                {
                    Interlocked.Decrement(ref _accountCount);
                }
            }
        }

        lock (_basisLock)
        {
            _basis = snapshot.To;
        }
    }
}
