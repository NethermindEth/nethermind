// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Nethermind.Blockchain;
using Nethermind.Consensus.Comparers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.TxPool.Collections;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.TxPool.Test;

/// <summary>Measures what the eth/72 serve path pays for reading a blob-carrying frame transaction twice: once
/// sidecar-free to learn it is a type-6, then whole because a type-6 sidecar must travel with it.</summary>
/// <remarks>Pool level, over a <c>MemColumnsDb</c>, so the figure is the cache lookups plus the RLP decode with no
/// disk seek. The two states are the ones the serve path can be in: warm, which is a hash this node announced and
/// a peer asked for straight after, and cold, which is the same hash after a restart or after the
/// <see cref="ITxPoolConfig.BlobCacheSize"/> LRUs have churned past it. A fresh pool over the same storage
/// reproduces cold exactly, since it restores the light collection with both caches empty.</remarks>
[TestFixture]
public class FrameTxServePathReadMeasurement
{
    private readonly List<Transaction> _samples = [];
    private readonly StringBuilder _report = new();

    /// <summary>Transactions the last timed pass actually answered.</summary>
    /// <remarks>A miss is the cheapest allocation-free route through both reads, so a pool that answers nothing
    /// would satisfy every other assertion here and read as an improvement in the table below.</remarks>
    private int _answered;

    [OneTimeSetUp]
    public void OneTimeSetup() => FrameTxBlobMeasurementHarness.EnsureKzgInitialized();

    /// <summary>The two properties the serve path's accepted double read rests on, asserted where CI runs them.</summary>
    /// <remarks>The measurement below is <c>[Explicit]</c>, so without this neither claim would be checked on any
    /// branch.</remarks>
    [Test]
    public void Serving_an_announced_frame_tx_reads_no_storage_and_never_escalates_to_a_full_row()
    {
        ServeReadCountingBlobTxStorage storage = BuildSamples(blobsPerTx: 1);
        using PersistentBlobTxDistinctSortedPool pool = InsertAll(storage);

        Transaction sample = _samples[0];
        Assert.That(storage.TryGetWithoutBlobs(sample.Hash!.ValueHash256, sample.SenderAddress!, out _), Is.True,
            "admission writes the sidecar-free record, so the sidecar-free read never falls back to the full row");

        AssertEverySampleIsServedWhole(pool);

        storage.ResetCounts();
        (TimeSpan _, long allocated) = TimeServePair(pool);

        Assert.Multiple(() =>
        {
            AssertThePassAnswered();
            Assert.That(storage.FullRowReads, Is.Zero, "a warm serve must not re-read the sidecar-carrying row");
            Assert.That(storage.SidecarFreeReads, Is.Zero, "a warm serve must not read the sidecar-free record either");
            Assert.That(allocated, Is.LessThan(8 * 1024),
                $"{FrameTxBlobMeasurementHarness.SampleTxs} warm serve pairs must answer from the pool's caches rather than decode any record");
        });
    }

    [TestCase(1)]
    [TestCase(6)]
    [Explicit("measurement harness")]
    public void Serving_a_blob_frame_tx_costs(int blobsPerTx)
    {
        ServeReadCountingBlobTxStorage storage = BuildSamples(blobsPerTx);
        using PersistentBlobTxDistinctSortedPool warm = InsertAll(storage);

        (TimeSpan warmPair, TimeSpan warmPairWorst, long warmPairAllocated) = FrameTxBlobMeasurementHarness.Measure(() => TimeServePair(warm));
        AssertThePassAnswered();
        (TimeSpan warmSingle, TimeSpan warmSingleWorst, long warmSingleAllocated) = FrameTxBlobMeasurementHarness.Measure(() => TimeFullReadOnly(warm));
        AssertThePassAnswered();

        // A fresh pool per pass keeps every sample cold: one pass touches each hash exactly once, and the
        // rebuild that resets the caches is outside the timer.
        (TimeSpan coldPair, TimeSpan coldPairWorst, long coldPairAllocated) = FrameTxBlobMeasurementHarness.Measure(() => TimeOnAColdPool(storage, TimeServePair));
        AssertThePassAnswered();
        (TimeSpan coldSingle, TimeSpan coldSingleWorst, long coldSingleAllocated) = FrameTxBlobMeasurementHarness.Measure(() => TimeOnAColdPool(storage, TimeFullReadOnly));
        AssertThePassAnswered();

        _report.AppendLine($"blobs per tx: {blobsPerTx}, samples: {FrameTxBlobMeasurementHarness.SampleTxs}, best of {FrameTxBlobMeasurementHarness.TimedPasses} after one discarded");
        AppendState("warm", warmPair, warmPairWorst, warmPairAllocated, warmSingle, warmSingleWorst, warmSingleAllocated);
        AppendState("cold", coldPair, coldPairWorst, coldPairAllocated, coldSingle, coldSingleWorst, coldSingleAllocated);
        _report.AppendLine();
    }

    [OneTimeTearDown]
    public void WriteReport() => FrameTxBlobMeasurementHarness.WriteReport(_report, "FRAME_SERVE_READ_OUT", "frame-serve-read.txt");

    /// <summary>Reports the best pass, with the worst beside it so a spread wide enough to swallow the difference
    /// between the two columns is visible rather than hidden behind one number.</summary>
    private void AppendState(string state, TimeSpan pair, TimeSpan pairWorst, long pairAllocated, TimeSpan single, TimeSpan singleWorst, long singleAllocated)
    {
        double pairUs = PerTxUs(pair);
        double singleUs = PerTxUs(single);
        _report.AppendLine($"  {state} pair {pairUs,8:N2}us single {singleUs,8:N2}us  discarded read {pairUs - singleUs,8:N2}us ({(pairUs / singleUs - 1) * 100,6:N1}%)");
        _report.AppendLine($"  {state} worst pass/tx pair {PerTxUs(pairWorst),8:N2}us single {PerTxUs(singleWorst),8:N2}us");
        _report.AppendLine($"  {state} allocated/tx  pair {pairAllocated / FrameTxBlobMeasurementHarness.SampleTxs,9:N0}B single {singleAllocated / FrameTxBlobMeasurementHarness.SampleTxs,9:N0}B");
    }

    private static double PerTxUs(TimeSpan elapsed) => elapsed.TotalMicroseconds / FrameTxBlobMeasurementHarness.SampleTxs;

    private void AssertThePassAnswered() => Assert.That(_answered, Is.EqualTo(FrameTxBlobMeasurementHarness.SampleTxs),
        "a pass that serves nothing is the cheapest of all, so a timing or allocation figure only means something once every sample was answered");

    /// <summary>Positive control: every sample must come back from both reads, as itself and whole.</summary>
    /// <remarks>The timed passes discard what they read, and a miss is the cheapest allocation-free route through
    /// both calls, so cheapness on its own is satisfied hardest by a pool that answers nothing. Checked over every
    /// sample rather than one, since a partial answer is the shape a regression would take.</remarks>
    private void AssertEverySampleIsServedWhole(BlobTxDistinctSortedPool pool)
    {
        foreach (Transaction sample in _samples)
        {
            ValueHash256 hash = sample.Hash!.ValueHash256;

            Assert.That(pool.TryGetValueWithoutBlobs(hash, out Transaction? elided), Is.True);
            Assert.That(elided!.Hash, Is.EqualTo(sample.Hash), "the sidecar-free read must answer with the sample itself");
            Assert.That(elided.Frames, Is.Not.Null.And.Length.EqualTo(sample.Frames!.Length), "it must keep the prefix");
            Assert.That(((ShardBlobNetworkWrapper)elided.NetworkWrapper!).Blobs, Is.Empty, "and drop the sidecar");

            Assert.That(pool.TryGetValue(hash, out Transaction? full), Is.True);
            Assert.That(full!.Hash, Is.EqualTo(sample.Hash), "the full read must answer with the sample itself");
            Assert.That(((ShardBlobNetworkWrapper)full.NetworkWrapper!).Blobs, Has.Length.EqualTo(sample.BlobVersionedHashes!.Length),
                "the type-6 serve is only correct if the sidecar travels with it");
        }
    }

    private ServeReadCountingBlobTxStorage BuildSamples(int blobsPerTx)
    {
        FrameTxBlobMeasurementHarness.BuildSamples(blobsPerTx, _samples);
        return new ServeReadCountingBlobTxStorage(new BlobTxStorage());
    }

    private PersistentBlobTxDistinctSortedPool InsertAll(ServeReadCountingBlobTxStorage storage)
    {
        PersistentBlobTxDistinctSortedPool pool = NewPool(storage);
        foreach (Transaction tx in _samples)
        {
            Assert.That(pool.TryInsert(tx.Hash!.ValueHash256, tx), Is.True);
        }

        return pool;
    }

    private static PersistentBlobTxDistinctSortedPool NewPool(ServeReadCountingBlobTxStorage storage)
    {
        // "Warm" means every sample is still cached, so the premise is set here rather than inherited from a
        // default that could change underneath it. This is the shipped default today.
        TxPoolConfig config = new() { BlobCacheSize = FrameTxBlobMeasurementHarness.SampleTxs };
        IComparer<Transaction> comparer = new TransactionComparerProvider(MainnetSpecProvider.Instance, Substitute.For<IBlockTree>()).GetDefaultComparer();
        return new PersistentBlobTxDistinctSortedPool(storage, config, comparer, LimboLogs.Instance);
    }

    private (TimeSpan, long) TimeOnAColdPool(ServeReadCountingBlobTxStorage storage, Func<BlobTxDistinctSortedPool, (TimeSpan, long)> pass)
    {
        using PersistentBlobTxDistinctSortedPool cold = NewPool(storage);
        return pass(cold);
    }

    /// <summary>What the serve path does: the sidecar-free read whose result the type-6 branch then discards,
    /// followed by the full read that answers the request.</summary>
    private (TimeSpan, long) TimeServePair(BlobTxDistinctSortedPool pool)
    {
        int answered = 0;
        long before = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();
        foreach (Transaction tx in _samples)
        {
            ValueHash256 hash = tx.Hash!.ValueHash256;
            bool servedSidecarFree = false;
            if (pool.TryGetValueWithoutBlobs(hash, out Transaction? elided))
            {
                _ = BlobTransactionPayload.Elide(elided);
                servedSidecarFree = true;
            }

            // Both, because the discarded read is the one being priced: pair - single is entirely its cost.
            if (servedSidecarFree && pool.TryGetValue(hash, out _)) answered++;
        }

        TimeSpan elapsed = Stopwatch.GetElapsedTime(start);
        _answered = answered;
        return (elapsed, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    /// <summary>What it would do if <see cref="ITxPool"/> exposed a type discriminator that needed no read.</summary>
    private (TimeSpan, long) TimeFullReadOnly(BlobTxDistinctSortedPool pool)
    {
        int answered = 0;
        long before = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();
        foreach (Transaction tx in _samples)
        {
            if (pool.TryGetValue(tx.Hash!.ValueHash256, out _)) answered++;
        }

        TimeSpan elapsed = Stopwatch.GetElapsedTime(start);
        _answered = answered;
        return (elapsed, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
