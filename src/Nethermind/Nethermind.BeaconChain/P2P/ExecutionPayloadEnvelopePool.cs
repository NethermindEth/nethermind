// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.Types;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;

namespace Nethermind.BeaconChain.P2P;

/// <summary>
/// In-memory cache of Gloas execution payload envelopes this node has received, serving
/// <c>ExecutionPayloadEnvelopesByRange</c>/<c>ByRoot</c>.
/// </summary>
/// <remarks>
/// A bounded recent-envelope cache only, evicted under capacity pressure with no epoch-based
/// retention guarantee, mirroring <see cref="DataColumnSidecarPool"/>'s own caveat. The slot-to-root
/// index is itself LRU-bounded to the same capacity, for the same reason: an unbounded map keyed by
/// slot would otherwise grow for as long as the process runs. It is last-write-wins and untracked
/// against reorgs.
/// </remarks>
public sealed class ExecutionPayloadEnvelopePool(int capacity = 1 << 12)
{
    private readonly LruCache<Hash256, SignedExecutionPayloadEnvelope> _byRoot = new(capacity, "execution payload envelopes");
    private readonly LruCache<ulong, Hash256> _rootBySlot = new(capacity, "execution payload envelopes by slot");

    /// <summary>The slot-to-root index's current entry count, bounded by <paramref name="capacity"/>; for tests and diagnostics.</summary>
    internal int SlotIndexCount => _rootBySlot.Count;

    public void Add(Hash256 blockRoot, ulong slot, SignedExecutionPayloadEnvelope envelope)
    {
        _byRoot.Set(blockRoot, envelope);
        _rootBySlot.Set(slot, blockRoot);
    }

    public bool TryGet(Hash256 blockRoot, out SignedExecutionPayloadEnvelope? envelope) =>
        _byRoot.TryGet(blockRoot, out envelope);

    public bool TryGet(ulong slot, out SignedExecutionPayloadEnvelope? envelope)
    {
        if (_rootBySlot.TryGet(slot, out Hash256? root))
        {
            return TryGet(root, out envelope);
        }

        envelope = null;
        return false;
    }
}
