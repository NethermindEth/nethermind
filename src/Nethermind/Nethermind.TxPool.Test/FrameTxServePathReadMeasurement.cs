// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using CkzgLib;
using Nethermind.Blockchain;
using Nethermind.Consensus.Comparers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
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
/// reproduces cold exactly, since it restores the light collection with both caches empty.
/// Results go to <c>FRAME_SERVE_READ_OUT</c>, or <c>frame-serve-read.txt</c> in the temp directory, because the
/// test runner swallows console writers.</remarks>
[TestFixture]
public class FrameTxServePathReadMeasurement
{
    private const int SampleTxs = 256;
    private const int TimedPasses = 5;

    private readonly List<Transaction> _samples = [];
    private readonly StringBuilder _report = new();

    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        if (!KzgPolynomialCommitments.IsInitialized) KzgPolynomialCommitments.InitializeAsync().Wait();
    }

    /// <summary>The two properties the serve path's accepted double read rests on, asserted where CI runs them.</summary>
    /// <remarks>The measurement below is <c>[Explicit]</c>, so without this neither claim would be checked on any
    /// branch. The allocation bound is what proves the warm pair never reaches storage: a pass that decoded one
    /// record would allocate orders of magnitude more than the whole 256-transaction pass is allowed here.</remarks>
    [Test]
    public void Serving_an_announced_frame_tx_reads_no_storage_and_never_escalates_to_a_full_row()
    {
        BlobTxStorage storage = BuildSamples(blobsPerTx: 1);
        BlobTxDistinctSortedPool pool = InsertAll(storage);

        Transaction sample = _samples[0];
        Assert.That(storage.TryGetWithoutBlobs(sample.Hash!.ValueHash256, sample.SenderAddress!, out _), Is.True,
            "admission writes the sidecar-free record, so the sidecar-free read never falls back to the full row");

        (TimeSpan _, long allocated) = TimeServePair(pool);
        Assert.That(allocated, Is.LessThan(8 * 1024),
            $"{SampleTxs} warm serve pairs must answer from the pool's caches rather than decode any record");
    }

    [TestCase(1)]
    [TestCase(6)]
    [Explicit("measurement harness")]
    public void Serving_a_blob_frame_tx_costs(int blobsPerTx)
    {
        BlobTxStorage storage = BuildSamples(blobsPerTx);
        BlobTxDistinctSortedPool warm = InsertAll(storage);

        (TimeSpan warmPair, long warmPairAllocated) = Measure(() => TimeServePair(warm));
        (TimeSpan warmSingle, long warmSingleAllocated) = Measure(() => TimeFullReadOnly(warm));

        // A fresh pool per pass keeps every sample cold: one pass touches each hash exactly once, and the
        // rebuild that resets the caches is outside the timer.
        (TimeSpan coldPair, long coldPairAllocated) = Measure(() => TimeServePair(NewPool(storage)));
        (TimeSpan coldSingle, long coldSingleAllocated) = Measure(() => TimeFullReadOnly(NewPool(storage)));

        _report.AppendLine($"blobs per tx: {blobsPerTx}, samples: {SampleTxs}, best of {TimedPasses} after one discarded");
        AppendState("warm", warmPair, warmPairAllocated, warmSingle, warmSingleAllocated);
        AppendState("cold", coldPair, coldPairAllocated, coldSingle, coldSingleAllocated);
        _report.AppendLine();
    }

    private void AppendState(string state, TimeSpan pair, long pairAllocated, TimeSpan single, long singleAllocated)
    {
        double pairUs = pair.TotalMicroseconds / SampleTxs;
        double singleUs = single.TotalMicroseconds / SampleTxs;
        _report.AppendLine($"  {state} pair {pairUs,8:N2}us single {singleUs,8:N2}us  discarded read {pairUs - singleUs,8:N2}us ({(pairUs / singleUs - 1) * 100,6:N1}%)");
        _report.AppendLine($"  {state} allocated/tx  pair {pairAllocated / SampleTxs,9:N0}B single {singleAllocated / SampleTxs,9:N0}B");
    }

    [OneTimeTearDown]
    public void WriteReport()
    {
        if (_report.Length == 0) return;

        string path = Environment.GetEnvironmentVariable("FRAME_SERVE_READ_OUT")
            ?? Path.Combine(Path.GetTempPath(), "frame-serve-read.txt");
        File.AppendAllText(path, _report.ToString());
        TestContext.Out.WriteLine(_report.ToString());
    }

    private BlobTxStorage BuildSamples(int blobsPerTx)
    {
        _samples.Clear();
        // One sidecar shared by every sample: the blobs differ only in bytes the decode does not branch on, and
        // computing cell proofs per transaction would dominate the setup.
        ShardBlobNetworkWrapper wrapper = BuildWrapper(blobsPerTx, out byte[][] versionedHashes);
        for (int i = 0; i < SampleTxs; i++)
        {
            _samples.Add(BuildBlobFrameTx(TestItem.Addresses[i % TestItem.Addresses.Length], (ulong)i, wrapper, versionedHashes));
        }

        return new BlobTxStorage();
    }

    private BlobTxDistinctSortedPool InsertAll(BlobTxStorage storage)
    {
        BlobTxDistinctSortedPool pool = NewPool(storage);
        foreach (Transaction tx in _samples)
        {
            Assert.That(pool.TryInsert(tx.Hash!.ValueHash256, tx), Is.True);
        }

        return pool;
    }

    private static BlobTxDistinctSortedPool NewPool(BlobTxStorage storage)
    {
        IComparer<Transaction> comparer = new TransactionComparerProvider(MainnetSpecProvider.Instance, Substitute.For<IBlockTree>()).GetDefaultComparer();
        return new PersistentBlobTxDistinctSortedPool(storage, new TxPoolConfig(), comparer, LimboLogs.Instance);
    }

    /// <summary>Runs <paramref name="pass"/> once discarded, then <see cref="TimedPasses"/> times.</summary>
    /// <remarks>Without the discarded pass the read that runs first is charged with JIT-compiling a decoder the
    /// second then finds warm, which lands on whichever side the caller happens to time first. The allocation
    /// figure is taken from the same best pass, so the two reported numbers describe one run rather than two.</remarks>
    private static (TimeSpan Best, long Allocated) Measure(Func<(TimeSpan, long)> pass)
    {
        pass();

        TimeSpan best = TimeSpan.MaxValue;
        long bestAllocated = 0;
        for (int i = 0; i < TimedPasses; i++)
        {
            (TimeSpan elapsed, long allocated) = pass();
            if (elapsed < best) (best, bestAllocated) = (elapsed, allocated);
        }

        return (best, bestAllocated);
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

    private static ShardBlobNetworkWrapper BuildWrapper(int blobCount, out byte[][] versionedHashes)
    {
        IBlobProofsManager proofsManager = IBlobProofsManager.For(ProofVersion.V1);
        byte[][] rawBlobs = new byte[blobCount][];
        for (int i = 0; i < blobCount; i++)
        {
            byte[] blob = new byte[Ckzg.BytesPerBlob];
            blob[0] = (byte)(i % 256);
            rawBlobs[i] = blob;
        }

        ShardBlobNetworkWrapper wrapper = proofsManager.AllocateWrapper(rawBlobs);
        proofsManager.ComputeProofsAndCommitments(wrapper);
        versionedHashes = proofsManager.ComputeHashes(wrapper);
        return wrapper;
    }

    private static Transaction BuildBlobFrameTx(Address sender, ulong nonce, ShardBlobNetworkWrapper wrapper, byte[][] versionedHashes)
    {
        Transaction tx = new()
        {
            Type = TxType.FrameTx,
            ChainId = TestBlockchainIds.ChainId,
            SenderAddress = sender,
            Nonce = nonce,
            GasLimit = 1_000_000,
            GasPrice = 1,
            DecodedMaxFeePerGas = 1.GWei,
            MaxFeePerBlobGas = 1.GWei,
            Frames = [FrameTxTestFrames.OnlyVerify(gasLimit: 40_000), FrameTxTestFrames.Pay(TestItem.AddressF, gasLimit: 40_000)],
            FrameSignatures = [],
            BlobVersionedHashes = versionedHashes,
            NetworkWrapper = wrapper,
        };
        tx.Hash = tx.CalculateHash();
        return tx;
    }
}
