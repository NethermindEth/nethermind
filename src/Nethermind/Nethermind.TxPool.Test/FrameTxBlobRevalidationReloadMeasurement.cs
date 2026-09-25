// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Nethermind.Core;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.TxPool.Test;

/// <summary>Measures what the head-lock revalidation sweep pays to recover a blob-carrying frame transaction's
/// prefix from blob storage: the full sidecar-carrying record against the sidecar-free one.</summary>
/// <remarks>Storage level only. It times <see cref="BlobTxStorage.TryGet"/> against
/// <see cref="BlobTxStorage.TryGetWithoutBlobs"/> over a <c>MemColumnsDb</c>, so the figure is the RLP decode plus
/// copy — the CPU the sweep holds <c>_newHeadLock</c>'s write lock for — with no disk seek. A real RocksDB only
/// widens the gap, so what this reports is a lower bound on the saving, not an estimate of it. That holds of the
/// byte ratio unconditionally, and of the timing ratio only because each read is warmed and then taken at its best
/// pass: timing two cold reads in sequence charges whichever ran first with the JIT the second then finds done.
/// The per-transaction cost is then extrapolated to <see cref="ITxPoolConfig.PersistentBlobStorageSize"/>, which
/// is the fan-out a reorg reaches through <c>CollectAll</c>.</remarks>
[TestFixture]
public class FrameTxBlobRevalidationReloadMeasurement
{
    private const int DefaultPersistentBlobStorageSize = 16384;

    private BlobTxStorage _storage = null!;
    private readonly List<Transaction> _samples = [];
    private readonly StringBuilder _report = new();

    [OneTimeSetUp]
    public void OneTimeSetup() => FrameTxBlobMeasurementHarness.EnsureKzgInitialized();

    [SetUp]
    public void Setup()
    {
        _storage = new BlobTxStorage();
        _samples.Clear();
    }

    /// <summary>The property the sweep's sidecar-free read is worth having, asserted where CI runs it.</summary>
    /// <remarks>The measurement below is <c>[Explicit]</c>, so without this the claim that the cheap read still
    /// carries the prefix would never be checked on any branch.</remarks>
    [Test]
    public void Sidecar_free_storage_read_keeps_the_prefix_and_drops_the_blobs()
    {
        StoreSamples(blobsPerTx: 1);
        AssertEveryFormIsReadable();
    }

    [TestCase(1)]
    [TestCase(6)]
    [Explicit("measurement harness")]
    public void Reloading_a_blob_frame_tx_prefix_costs(int blobsPerTx)
    {
        StoreSamples(blobsPerTx);

        int fullBytes = Encoded(_samples[0], RlpBehaviors.InMempoolForm | RlpBehaviors.Storage).Length;
        int elidedBytes = Encoded(BlobTransactionPayload.Elide(_samples[0]), RlpBehaviors.InMempoolForm).Length;

        AssertEveryFormIsReadable();

        (TimeSpan fullBest, TimeSpan fullWorst, long fullAllocated) = FrameTxBlobMeasurementHarness.Measure(TimeFull);
        (TimeSpan elidedBest, TimeSpan elidedWorst, long elidedAllocated) = FrameTxBlobMeasurementHarness.Measure(TimeElided);

        double fullPerTxUs = fullBest.TotalMicroseconds / FrameTxBlobMeasurementHarness.SampleTxs;
        double elidedPerTxUs = elidedBest.TotalMicroseconds / FrameTxBlobMeasurementHarness.SampleTxs;

        _report.AppendLine($"blobs per tx: {blobsPerTx}, samples: {FrameTxBlobMeasurementHarness.SampleTxs}, timed passes: {FrameTxBlobMeasurementHarness.TimedPasses} after one discarded; read+decode and allocated are both the best pass");
        _report.AppendLine($"  record bytes   full {fullBytes,10:N0}   elided {elidedBytes,10:N0}   ratio {(double)fullBytes / elidedBytes,8:N1}x");
        _report.AppendLine($"  read+decode/tx full {fullPerTxUs,10:N1}us elided {elidedPerTxUs,10:N1}us ratio {fullPerTxUs / elidedPerTxUs,8:N1}x");
        _report.AppendLine($"  worst pass/tx  full {fullWorst.TotalMicroseconds / FrameTxBlobMeasurementHarness.SampleTxs,10:N1}us elided {elidedWorst.TotalMicroseconds / FrameTxBlobMeasurementHarness.SampleTxs,10:N1}us ratio {fullWorst.TotalMicroseconds / elidedWorst.TotalMicroseconds,8:N1}x");
        _report.AppendLine($"  allocated/tx   full {fullAllocated / FrameTxBlobMeasurementHarness.SampleTxs,10:N0}B  elided {elidedAllocated / FrameTxBlobMeasurementHarness.SampleTxs,10:N0}B");
        _report.AppendLine($"  CollectAll over {DefaultPersistentBlobStorageSize:N0} under the head write lock:");
        _report.AppendLine($"    full   {fullPerTxUs * DefaultPersistentBlobStorageSize / 1_000_000,10:N1}s  ({(double)fullBytes * DefaultPersistentBlobStorageSize / (1 << 30),0:N1} GiB decoded)");
        _report.AppendLine($"    elided {elidedPerTxUs * DefaultPersistentBlobStorageSize / 1_000_000,10:N1}s  ({(double)elidedBytes * DefaultPersistentBlobStorageSize / (1 << 30),0:N1} GiB decoded)");
        _report.AppendLine();
    }

    private void StoreSamples(int blobsPerTx)
    {
        FrameTxBlobMeasurementHarness.BuildSamples(blobsPerTx, _samples);
        foreach (Transaction tx in _samples)
        {
            _storage.Add(tx);
        }
    }

    [OneTimeTearDown]
    public void WriteReport() => FrameTxBlobMeasurementHarness.WriteReport(_report, "FRAME_BLOB_RELOAD_OUT", "frame-blob-reload.txt");

    /// <summary>Positive control: a timing pair means nothing if either read is answering with nothing, or if the
    /// sidecar-free form has lost the prefix the sweep reads.</summary>
    private void AssertEveryFormIsReadable()
    {
        Transaction sample = _samples[0];
        Assert.That(_storage.TryGet(sample.Hash!.ValueHash256, sample.SenderAddress!, sample.Timestamp, out Transaction? full), Is.True);
        Assert.That(full!.NetworkWrapper, Is.InstanceOf<ShardBlobNetworkWrapper>(), "the full read must carry the sidecar it is timed for");

        Assert.That(_storage.TryGetWithoutBlobs(sample.Hash!.ValueHash256, sample.SenderAddress!, out Transaction? elided), Is.True);
        Assert.That(((ShardBlobNetworkWrapper)elided!.NetworkWrapper!).Blobs, Is.Empty, "the sidecar-free read must not carry blobs");
        Assert.That(elided.Frames, Is.Not.Null.And.Length.EqualTo(sample.Frames!.Length),
            "the sidecar-free form is only useful to revalidation if it keeps the prefix");
    }

    private (TimeSpan, long) TimeFull()
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();
        foreach (Transaction tx in _samples)
        {
            _storage.TryGet(tx.Hash!.ValueHash256, tx.SenderAddress!, tx.Timestamp, out _);
        }

        return (Stopwatch.GetElapsedTime(start), GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private (TimeSpan, long) TimeElided()
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();
        foreach (Transaction tx in _samples)
        {
            _storage.TryGetWithoutBlobs(tx.Hash!.ValueHash256, tx.SenderAddress!, out _);
        }

        return (Stopwatch.GetElapsedTime(start), GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private static byte[] Encoded(Transaction tx, RlpBehaviors behaviors) => TxDecoder.Instance.Encode(tx, behaviors).Bytes;
}
