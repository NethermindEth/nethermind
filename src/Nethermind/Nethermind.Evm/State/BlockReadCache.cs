// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Int256;

namespace Nethermind.Evm.State;

/// <summary>The state a block stands on, as read by the transactions traced against it. Every transaction of a block
/// resolves the same parent state, and each one resolves it on its own: the busy contracts of a block are read once
/// per transaction, and each of those reads is a seek into history rather than a page fetch, so they are paid in CPU
/// even when the rows are already in memory. Holding them for the block collapses that to one read per key.
/// <para>What is cached is the state underneath the per-transaction overlay, which is the same for every transaction
/// of the block; the overlay is applied over a cached value exactly as it is over a freshly read one.</para></summary>
public sealed class BlockReadCache(int accountSetsBits = 12, int storageSetsBits = 14)
{
    private readonly SeqlockCache<AddressAsKey, Account> _accounts = new(accountSetsBits);
    private readonly SeqlockCache<StorageCell, UInt256> _slots = new(storageSetsBits);
    private uint _clears;

    /// <summary>Invalidates the previous parent state. Call only after all workers have released this cache.</summary>
    /// <returns>False when the epoch budget is exhausted and the cache must be discarded instead of reused.</returns>
    public bool Clear()
    {
        if (_clears >= (1U << 26) - 1) return false;
        _clears++;
        _accounts.Clear();
        _slots.Clear();
        return true;
    }

    /// <summary>Returns a cached parent account; true with null means known absent, false means a cache miss.</summary>
    public bool TryGetAccount(Address address, out Account? account)
    {
        AddressAsKey key = address;
        return _accounts.TryGetValue(in key, out account);
    }

    /// <summary>Caches a parent-state account or its absence. Do not mix parent states in one cache.</summary>
    public void SetAccount(Address address, Account? account)
    {
        AddressAsKey key = address;
        _accounts.Set(in key, account);
    }

    /// <summary>Returns a cached parent slot, including known zero. Ignore the output when false.</summary>
    public bool TryGetSlot(Address address, in UInt256 index, out UInt256 value)
    {
        StorageCell cell = new(address, in index);
        return _slots.TryGetValue(in cell, out value);
    }

    /// <summary>Caches a parent-state slot; concurrent accesses are supported.</summary>
    public void SetSlot(Address address, in UInt256 index, in UInt256 value)
    {
        StorageCell cell = new(address, in index);
        _slots.Set(in cell, in value);
    }
}
