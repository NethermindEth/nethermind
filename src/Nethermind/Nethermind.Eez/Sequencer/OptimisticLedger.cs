// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Eez.Sequencer;

/// <summary>A batch the sequencer posted and the Sync block it settles.</summary>
public sealed record PostedBatch(ulong SyncHeight, Hash256 Transaction, BlockHeader Parent);

/// <summary>
/// The batches posted but not yet settled on L1 as the follower sees it. What L1 settles is decided by the follower's
/// cursor, not by the observer of a submission: a batch at or below the cursor is settled whatever the observer said,
/// and one above it keeps the next batch from being posted even when the observer saw it land, since both would start
/// from the same cursor and the second would revert.
/// </summary>
public sealed class OptimisticLedger
{
    private readonly Lock _lock = new();
    private readonly SortedDictionary<ulong, Entry> _batches = [];

    public void Begin(PostedBatch batch)
    {
        lock (_lock)
        {
            _batches[batch.SyncHeight] = new Entry(batch);
        }
    }

    /// <param name="l1Block">The L1 block the observer saw the batch settle in.</param>
    public void MarkSettled(ulong syncHeight, ulong l1Block)
    {
        lock (_lock)
        {
            if (_batches.TryGetValue(syncHeight, out Entry? entry))
            {
                entry.Verdict = Verdict.Settled;
                entry.SettledIn = l1Block;
            }
        }
    }

    /// <param name="slotSkipped">The pinned L1 block was built at another timestamp, so the batch never had its slot.</param>
    public void MarkFailed(ulong syncHeight, bool slotSkipped)
    {
        lock (_lock)
        {
            if (_batches.TryGetValue(syncHeight, out Entry? entry) && entry.Verdict == Verdict.Pending)
            {
                entry.Verdict = Verdict.Failed;
                entry.SlotSkipped = slotSkipped;
            }
        }
    }

    /// <summary>Forgets every batch the cursor settled.</summary>
    public void ConfirmThrough(ulong cursor)
    {
        lock (_lock)
        {
            foreach (ulong height in _batches.Keys.TakeWhile(h => h <= cursor).ToArray())
            {
                _batches.Remove(height);
            }
        }
    }

    /// <summary>
    /// Fails the batches above <paramref name="cursor"/> the observer saw settle in an L1 block at or below
    /// <paramref name="l1Followed"/>: the follower has read that far without its cursor reaching them, so the block they
    /// settled in left the chain before the follower saw it, and they are posted again.
    /// </summary>
    public void ExpireUnconfirmed(ulong cursor, ulong l1Followed)
    {
        lock (_lock)
        {
            foreach ((ulong height, Entry entry) in _batches)
            {
                if (height > cursor && entry.Verdict == Verdict.Settled && entry.SettledIn <= l1Followed)
                {
                    entry.Verdict = Verdict.Failed;
                    entry.SlotSkipped = false;
                }
            }
        }
    }

    /// <summary>Whether a batch above <paramref name="cursor"/> is still unsettled on L1, so no other may be posted.</summary>
    public bool Blocks(ulong cursor)
    {
        lock (_lock)
        {
            return _batches.Count > 0 && _batches.Keys.Last() > cursor;
        }
    }

    /// <returns>The lowest failed batch above <paramref name="cursor"/>, removed, or <see langword="null"/>.</returns>
    public (PostedBatch Batch, bool SlotSkipped)? TakeFailed(ulong cursor)
    {
        lock (_lock)
        {
            foreach ((ulong height, Entry entry) in _batches)
            {
                if (height > cursor && entry.Verdict == Verdict.Failed)
                {
                    _batches.Remove(height);
                    return (entry.Batch, entry.SlotSkipped);
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Forgets the batches above <paramref name="cursor"/> the observer saw settle: an L1 reorganization took them out,
    /// so they are posted again. Pending and failed ones are left to their observers and to recovery.
    /// </summary>
    public void RollBack(ulong cursor)
    {
        lock (_lock)
        {
            foreach (ulong height in _batches.Where(b => b.Key > cursor && b.Value.Verdict == Verdict.Settled).Select(static b => b.Key).ToArray())
            {
                _batches.Remove(height);
            }
        }
    }

    private enum Verdict
    {
        Pending,
        Settled,
        Failed,
    }

    private sealed class Entry(PostedBatch batch)
    {
        public PostedBatch Batch { get; } = batch;

        public Verdict Verdict { get; set; }

        public bool SlotSkipped { get; set; }

        public ulong SettledIn { get; set; }
    }
}
