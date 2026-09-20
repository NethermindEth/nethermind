// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;

namespace Nethermind.BeaconChain.P2P;

/// <summary>
/// In-memory cache of data column sidecars this node has validated, serving
/// <c>DataColumnSidecarsByRange</c>/<c>ByRoot</c>.
/// </summary>
/// <remarks>
/// This is a bounded recent-sidecar cache, not the spec-required persisted store (a node MUST be
/// able to serve these requests for <see cref="Eip7594DasConstants.MinEpochsForDataColumnSidecarsRequests"/>
/// epochs): entries are evicted under capacity pressure like any LRU cache, with no epoch-based
/// retention guarantee. A durable, retention-window-honoring store belongs with <c>Storage/</c>.
/// The slot-to-root index is last-write-wins and untracked against reorgs: a sidecar added for a
/// slot that is later reorged out is only replaced once a sidecar for the new canonical block at
/// that slot is added.
/// </remarks>
public sealed class DataColumnSidecarPool(int capacity = 1 << 14)
{
    private readonly LruCache<(Hash256 BlockRoot, ulong Column), DataColumnSidecar> _byRootAndColumn = new(capacity, "data column sidecars");
    private readonly ConcurrentDictionary<ulong, Hash256> _rootBySlot = new();

    public void Add(Hash256 blockRoot, ulong slot, DataColumnSidecar sidecar)
    {
        _byRootAndColumn.Set((blockRoot, sidecar.Index), sidecar);
        _rootBySlot[slot] = blockRoot;
    }

    public bool TryGet(Hash256 blockRoot, ulong column, out DataColumnSidecar? sidecar) =>
        _byRootAndColumn.TryGet((blockRoot, column), out sidecar);

    public bool TryGet(ulong slot, ulong column, out DataColumnSidecar? sidecar)
    {
        if (_rootBySlot.TryGetValue(slot, out Hash256? root))
        {
            return TryGet(root, column, out sidecar);
        }

        sidecar = null;
        return false;
    }
}
