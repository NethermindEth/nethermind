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

    [OneTimeSetUp]
    public void OneTimeSetup() => FrameTxBlobMeasurementHarness.EnsureKzgInitialized();

    /// <summary>The two properties the serve path's accepted double read rests on, asserted where CI runs them.</summary>
    /// <remarks>The measurement below is <c>[Explicit]</c>, so without this neither claim would be checked on any
    /// branch.</remarks>
    [Test]
    public void Serving_an_announced_frame_tx_reads_no_storage_and_never_escalates_to_a_full_row()
    {
        CountingBlobTxStorage storage = BuildSamples(blobsPerTx: 1);
        using PersistentBlobTxDistinctSortedPool pool = InsertAll(storage);

        Transaction sample = _samples[0];
        Assert.That(storage.TryGetWithoutBlobs(sample.Hash!.ValueHash256, sample.SenderAddress!, out _), Is.True,
            "admission writes the sidecar-free record, so the sidecar-free read never falls back to the full row");

        storage.ResetCounts();
        (TimeSpan _, long allocated) = TimeServePair(pool);

        Assert.Multiple(() =>
        {
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
        CountingBlobTxStorage storage = BuildSamples(blobsPerTx);
        using PersistentBlobTxDistinctSortedPool warm = InsertAll(storage);

        (TimeSpan warmPair, TimeSpan _, long warmPairAllocated) = FrameTxBlobMeasurementHarness.Measure(() => TimeServePair(warm));
        (TimeSpan warmSingle, TimeSpan _, long warmSingleAllocated) = FrameTxBlobMeasurementHarness.Measure(() => TimeFullReadOnly(warm));

        // A fresh pool per pass keeps every sample cold: one pass touches each hash exactly once, and the
        // rebuild that resets the caches is outside the timer.
        (TimeSpan coldPair, TimeSpan _, long coldPairAllocated) = FrameTxBlobMeasurementHarness.Measure(() => TimeOnAColdPool(storage, TimeServePair));
        (TimeSpan coldSingle, TimeSpan _, long coldSingleAllocated) = FrameTxBlobMeasurementHarness.Measure(() => TimeOnAColdPool(storage, TimeFullReadOnly));

        _report.AppendLine($"blobs per tx: {blobsPerTx}, samples: {FrameTxBlobMeasurementHarness.SampleTxs}, best of {FrameTxBlobMeasurementHarness.TimedPasses} after one discarded");
        AppendState("warm", warmPair, warmPairAllocated, warmSingle, warmSingleAllocated);
        AppendState("cold", coldPair, coldPairAllocated, coldSingle, coldSingleAllocated);
        _report.AppendLine();
    }

    [OneTimeTearDown]
    public void WriteReport() => FrameTxBlobMeasurementHarness.WriteReport(_report, "FRAME_SERVE_READ_OUT", "frame-serve-read.txt");

    private void AppendState(string state, TimeSpan pair, long pairAllocated, TimeSpan single, long singleAllocated)
    {
        double pairUs = pair.TotalMicroseconds / FrameTxBlobMeasurementHarness.SampleTxs;
        double singleUs = single.TotalMicroseconds / FrameTxBlobMeasurementHarness.SampleTxs;
        _report.AppendLine($"  {state} pair {pairUs,8:N2}us single {singleUs,8:N2}us  discarded read {pairUs - singleUs,8:N2}us ({(pairUs / singleUs - 1) * 100,6:N1}%)");
        _report.AppendLine($"  {state} allocated/tx  pair {pairAllocated / FrameTxBlobMeasurementHarness.SampleTxs,9:N0}B single {singleAllocated / FrameTxBlobMeasurementHarness.SampleTxs,9:N0}B");
    }

    private CountingBlobTxStorage BuildSamples(int blobsPerTx)
    {
        FrameTxBlobMeasurementHarness.BuildSamples(blobsPerTx, _samples);
        return new CountingBlobTxStorage(new BlobTxStorage());
    }

    private PersistentBlobTxDistinctSortedPool InsertAll(CountingBlobTxStorage storage)
    {
        PersistentBlobTxDistinctSortedPool pool = NewPool(storage);
        foreach (Transaction tx in _samples)
        {
            Assert.That(pool.TryInsert(tx.Hash!.ValueHash256, tx), Is.True);
        }

        return pool;
    }

    private static PersistentBlobTxDistinctSortedPool NewPool(CountingBlobTxStorage storage)
    {
        TxPoolConfig config = new();
        // "Warm" below means every sample is still cached, which stops holding the moment the samples outnumber
        // the LRU. Asserted here rather than left to an allocation figure nobody could trace back to the cause.
        Assert.That(config.BlobCacheSize, Is.GreaterThanOrEqualTo(FrameTxBlobMeasurementHarness.SampleTxs),
            $"{nameof(ITxPoolConfig.BlobCacheSize)} must hold every sample for the warm figures to mean anything");

        IComparer<Transaction> comparer = new TransactionComparerProvider(MainnetSpecProvider.Instance, Substitute.For<IBlockTree>()).GetDefaultComparer();
        return new PersistentBlobTxDistinctSortedPool(storage, config, comparer, LimboLogs.Instance);
    }

    private (TimeSpan, long) TimeOnAColdPool(CountingBlobTxStorage storage, Func<BlobTxDistinctSortedPool, (TimeSpan, long)> pass)
    {
        using PersistentBlobTxDistinctSortedPool cold = NewPool(storage);
        return pass(cold);
    }

    /// <summary>What the serve path does: the sidecar-free read whose result the type-6 branch then discards,
    /// followed by the full read that answers the request.</summary>
    private (TimeSpan, long) TimeServePair(BlobTxDistinctSortedPool pool)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();
        foreach (Transaction tx in _samples)
        {
            ValueHash256 hash = tx.Hash!.ValueHash256;
            if (pool.TryGetValueWithoutBlobs(hash, out Transaction? elided))
            {
                _ = BlobTransactionPayload.Elide(elided);
            }

            pool.TryGetValue(hash, out _);
        }

        return (Stopwatch.GetElapsedTime(start), GC.GetAllocatedBytesForCurrentThread() - before);
    }

    /// <summary>What it would do if <see cref="ITxPool"/> exposed a type discriminator that needed no read.</summary>
    private (TimeSpan, long) TimeFullReadOnly(BlobTxDistinctSortedPool pool)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();
        foreach (Transaction tx in _samples)
        {
            pool.TryGetValue(tx.Hash!.ValueHash256, out _);
        }

        return (Stopwatch.GetElapsedTime(start), GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
