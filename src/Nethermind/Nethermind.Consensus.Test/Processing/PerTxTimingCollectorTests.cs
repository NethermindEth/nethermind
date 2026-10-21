// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Processing;
using Nethermind.Core.Collections;
using Nethermind.Core.Threading;
using NUnit.Framework;

#nullable enable

namespace Nethermind.Consensus.Test.Processing;

/// <summary>
/// Exercises <see cref="PerTxTimingCollector"/>'s shared-static contract under the same kind of
/// parallel write pattern <see cref="BlockProcessor.ParallelBlockValidationTransactionsExecutor"/>
/// produces: many worker threads call <see cref="PerTxTimingCollector.Record"/> after
/// <see cref="PerTxTimingCollector.Prepare"/>, and the block-processing thread that called
/// <see cref="PerTxTimingCollector.Snapshot"/> after the join sees every recorded value.
/// </summary>
[TestFixture]
public class PerTxTimingCollectorTests
{
    [SetUp]
    public void Setup() => PerTxTimingCollector.SetEnabled(true);

    [TearDown]
    public void TearDown() => PerTxTimingCollector.SetEnabled(false);

    [Test]
    public void Parallel_workers_recording_concurrently_are_all_visible_in_snapshot()
    {
        // Mirrors the parallel executor's setup: Prepare on the calling thread, workers fan out
        // through ParallelUnbalancedWork.For (which is the join barrier the collector's threading
        // contract relies on), then Snapshot on the calling thread reads the result.
        const int txCount = 512;

        PerTxTimingCollector.Prepare(txCount);

        // Each worker writes a unique non-zero value into its slot. Using `i + 1` so we can later
        // tell a stale-zero slot apart from one that was correctly written by the worker.
        ParallelUnbalancedWork.For(
            0,
            txCount,
            ParallelUnbalancedWork.DefaultOptions,
            txCount,
            static (i, _) =>
            {
                PerTxTimingCollector.Record(i, i + 1L);
                return 0;
            });

        using ArrayPoolList<long>? snapshot = PerTxTimingCollector.Snapshot();

        Assert.That(snapshot, Is.Not.Null);
        Assert.That(snapshot!.Count, Is.EqualTo(txCount));
        for (int i = 0; i < txCount; i++)
        {
            Assert.That(snapshot[i], Is.EqualTo(i + 1L), $"slot {i}");
        }
    }

    [Test]
    public void Snapshot_after_prepare_with_no_records_returns_zero_filled_handoff()
    {
        // Prepare allocates a length-N list pre-cleared to zero; if no worker records anything
        // (e.g. zero-tx block), Snapshot still hands off a Count==N list of zeros, not null.
        // null is reserved for the disabled-collector path tested below.
        PerTxTimingCollector.Prepare(8);

        using ArrayPoolList<long>? snapshot = PerTxTimingCollector.Snapshot();

        Assert.That(snapshot, Is.Not.Null);
        Assert.That(snapshot!.Count, Is.EqualTo(8));
        Assert.That(snapshot.AsSpan().ToArray(), Is.All.EqualTo(0L));
    }

    [Test]
    public void Snapshot_returns_null_when_collector_is_disabled()
    {
        PerTxTimingCollector.SetEnabled(false);
        PerTxTimingCollector.Prepare(4);

        ArrayPoolList<long>? snapshot = PerTxTimingCollector.Snapshot();

        Assert.That(snapshot, Is.Null);
    }

    [Test]
    public void Snapshot_hands_off_ownership_so_next_prepare_starts_clean()
    {
        // First block: Prepare → Record → Snapshot hands off. Next Prepare must give us a fresh
        // list; the previous handoff must not be mutated by subsequent Record calls.
        PerTxTimingCollector.Prepare(2);
        PerTxTimingCollector.Record(0, 100L);
        PerTxTimingCollector.Record(1, 200L);
        using ArrayPoolList<long>? first = PerTxTimingCollector.Snapshot();

        PerTxTimingCollector.Prepare(2);
        PerTxTimingCollector.Record(0, 999L);
        using ArrayPoolList<long>? second = PerTxTimingCollector.Snapshot();

        Assert.That(first, Is.Not.Null);
        Assert.That(first![0], Is.EqualTo(100L));
        Assert.That(first[1], Is.EqualTo(200L));

        Assert.That(second, Is.Not.Null);
        Assert.That(second![0], Is.EqualTo(999L));
        Assert.That(second[1], Is.EqualTo(0L), "previous block's write must not leak into the new buffer");
    }

    [Test]
    public void Record_for_out_of_range_index_is_silently_dropped()
    {
        // Defensive guard in the parallel executor: if for any reason a worker computes an index
        // beyond the prepared range, the collector no-ops rather than throwing — the test contract
        // here keeps that property stable so the executor can rely on it.
        PerTxTimingCollector.Prepare(4);
        PerTxTimingCollector.Record(99, 42L);
        PerTxTimingCollector.Record(-1, 42L);

        using ArrayPoolList<long>? snapshot = PerTxTimingCollector.Snapshot();
        Assert.That(snapshot, Is.Not.Null);
        Assert.That(snapshot!.AsSpan().ToArray(), Is.All.EqualTo(0L));
    }
}
