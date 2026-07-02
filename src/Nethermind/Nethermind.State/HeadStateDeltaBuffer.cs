// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Frozen;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.State;

/// <summary>
/// The accounts and storage cells a processed block changed, captured from the world-state write batches
/// (see <see cref="HeadStateDeltaCaptureScopeProvider"/>).
/// </summary>
/// <param name="ChangedAccounts">Accounts the block mutated (balance/nonce/code or moved storage root).</param>
/// <param name="ChangedSlots">Storage cells the block wrote. Empty when only accounts changed.</param>
/// <param name="RequiresFlush">
/// True when the block self-destructed/cleared an account's storage: the full set of affected slots is
/// not enumerable, so the consumer must flush the cache rather than apply a partial delta.
/// </param>
public readonly record struct HeadStateBlockDelta(
    FrozenSet<AddressAsKey> ChangedAccounts,
    FrozenSet<StorageCell> ChangedSlots,
    bool RequiresFlush);

/// <summary>
/// A small bounded store of recent per-block deltas, written by the processing-side capture decorator and
/// read by <c>HeadStateCacheUpdater</c> when the head advances. Keyed by <c>(blockNumber, post-state root)</c>
/// so the consumer can match the canonical block via its number + <see cref="BlockHeader.StateRoot"/>
/// regardless of when the block becomes canonical.
/// </summary>
/// <remarks>
/// The key is not perfectly collision-free: two blocks at the same number reaching the same post-state via
/// different parents would collide. That is vanishingly rare, and the only consequence is an under-/over-
/// inclusive delta for one block — the head cache then serves a stale head-X read at worst. The buffer is
/// bounded; mid-block intermediate roots are evicted in insertion order.
/// </remarks>
public sealed class HeadStateDeltaBuffer(int capacity = 64)
{
    private readonly Lock _lock = new();
    private readonly Queue<(long Number, Hash256 Root)> _order = new();
    private readonly Dictionary<(long Number, Hash256 Root), HeadStateBlockDelta> _byKey = [];

    public void Store(ulong blockNumber, Hash256 stateRoot, HeadStateBlockDelta delta)
    {
        (long, Hash256) key = ((long)blockNumber, stateRoot);
        lock (_lock)
        {
            if (!_byKey.ContainsKey(key))
            {
                _order.Enqueue(key);
                while (_order.Count > capacity)
                {
                    _byKey.Remove(_order.Dequeue());
                }
            }
            _byKey[key] = delta;
        }
    }

    public bool TryGet(ulong blockNumber, Hash256 stateRoot, out HeadStateBlockDelta delta)
    {
        lock (_lock)
        {
            return _byKey.TryGetValue(((long)blockNumber, stateRoot), out delta);
        }
    }
}
