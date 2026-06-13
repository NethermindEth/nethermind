// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Nethermind.Core.Crypto;

namespace Nethermind.Consensus.Processing;

/// <summary>
/// Shared hand-off between the idle transaction-pool pre-warmer and the arrival-time block
/// pre-warmer. The idle warmer publishes, keyed by the head it warmed against, the set of
/// transaction hashes whose state it has already pulled into the database caches. When the next
/// block builds on exactly that head, the arrival pre-warmer skips those transactions and spends
/// its parallel cold-read budget only on the remainder (the private/unseen transactions) — the
/// head start it otherwise cannot achieve on a uniformly cold block.
///
/// Correctness: this only influences WHICH transactions the pre-warmer speculatively executes,
/// never what the main block-processing path reads. A stale or mismatched entry can at worst cost
/// a cache miss, never a wrong result. The parent-hash guard makes a reorg a no-op — the published
/// set simply does not match the arriving block's parent.
/// </summary>
public sealed class IdleWarmedSet
{
    private volatile Entry? _current;

    public void Publish(Hash256 warmedAgainstHead, ConcurrentDictionary<Hash256, bool> warmedHashes)
        => _current = new Entry(warmedAgainstHead, warmedHashes);

    public ConcurrentDictionary<Hash256, bool>? GetFor(Hash256? parentHash)
    {
        Entry? entry = _current;
        return entry is not null && parentHash is not null && entry.WarmedAgainstHead == parentHash
            ? entry.WarmedHashes
            : null;
    }

    private sealed record Entry(Hash256 WarmedAgainstHead, ConcurrentDictionary<Hash256, bool> WarmedHashes);
}
