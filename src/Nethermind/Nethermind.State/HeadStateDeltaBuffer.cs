// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Frozen;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.State;

/// <summary>
/// The set of storage cells a processed block changed, captured from the world-state write batch
/// (see <see cref="HeadStateDeltaCaptureScopeProvider"/>) and keyed by the block's post-state root.
/// </summary>
/// <param name="ChangedSlots">Storage cells the block wrote. Empty when only accounts changed.</param>
/// <param name="RequiresFlush">
/// True when the block self-destructed/cleared an account's storage: the full set of affected slots is
/// not enumerable, so the consumer must flush the cache rather than apply a partial delta.
/// </param>
public readonly record struct HeadStateBlockDelta(FrozenSet<StorageCell> ChangedSlots, bool RequiresFlush);

/// <summary>
/// A small bounded store of recent per-block storage deltas, written by the processing-side capture
/// decorator and read by <c>HeadStateCacheUpdater</c> when the head advances. Keyed by post-state root
/// so the consumer can match the canonical block via its <see cref="BlockHeader.StateRoot"/> regardless
/// of when the block becomes canonical.
/// </summary>
public sealed class HeadStateDeltaBuffer(int capacity = 64)
{
    private readonly Lock _lock = new();
    private readonly Queue<Hash256> _order = new();
    private readonly Dictionary<Hash256, HeadStateBlockDelta> _byRoot = [];

    public void Store(Hash256 stateRoot, HeadStateBlockDelta delta)
    {
        lock (_lock)
        {
            if (!_byRoot.ContainsKey(stateRoot))
            {
                _order.Enqueue(stateRoot);
                while (_order.Count > capacity)
                {
                    _byRoot.Remove(_order.Dequeue());
                }
            }
            _byRoot[stateRoot] = delta;
        }
    }

    public bool TryGet(Hash256 stateRoot, out HeadStateBlockDelta delta)
    {
        lock (_lock)
        {
            return _byRoot.TryGetValue(stateRoot, out delta);
        }
    }
}
