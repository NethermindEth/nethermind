// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.TxPool;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test;

/// <summary>
/// Measures mempool rejection latency for an EIP-8141 validation prefix that spends its whole budget on
/// cold <c>SLOAD</c>s, against a real RocksDB-backed state database rather than the in-memory one every
/// other frame-tx harness uses.
/// </summary>
/// <remarks>
/// The prefix, the slot scatter and the RocksDB-backed chain are shared with
/// <see cref="FrameTxStorageProducerMeasurement"/>; see <see cref="ColdSloadPrefix"/> and
/// <see cref="ColdSloadTestBlockchain"/>.
///
/// A single number would be meaningless here, because the answer depends entirely on where the slot is
/// resident. Each ceiling therefore emits one row per rung of a cache-residency ladder; the rungs are
/// described on <see cref="Rungs"/>. Every row carries the bytes this process actually pulled from the
/// storage layer during its timed window (<c>read_bytes</c> from <c>/proc/self/io</c>), so a row that
/// silently measured a warm cache is visible as such rather than being mistaken for a device read.
///
/// Every row also names the environment it ran in rather than the one it asked for: <c>db_fs</c> is the
/// file system the database sits on, and <c>trie_cache_mb</c> the trie-store cache the container resolved.
/// A run whose temp directory is <c>tmpfs</c> has no block device under it, so no rung can reach one
/// whatever the caches do; a run whose trie cache is smaller than the seeded state has no warm rung.
///
/// Results are appended as <c>RESULT key=value</c> lines to <c>FRAME_STORAGE_IO_OUT</c>, or
/// <c>frame-tx-storage-io.txt</c> in the temp directory. The <c>case=frame_reject</c> rows carry the same
/// keys as <c>FrameTxMempoolDosMeasurement</c>'s, so existing parsing keeps working; the ladder-specific
/// fields are additions.
///
/// Run under <c>taskset -c 0</c>, like the rest of the campaign.
/// </remarks>
[TestFixture]
[Explicit("measurement harness")]
[NonParallelizable]
public class FrameTxStorageIoMeasurement
{
    /// <summary>Untimed submissions used to warm the interpreter and the JIT.</summary>
    private const int Warmup = 40;

    /// <summary>
    /// Timed submissions per rung. Far below the CPU harnesses' 1,000 because every cold sample needs its
    /// own never-read slot range, and the seeded slot count is the fixture's dominant cost.
    /// </summary>
    private const int Samples = 200;

    private static readonly Address Attacker = TestItem.AddressF;
    private static readonly UInt256 AttackerBalance = 1_000.Ether;

    /// <summary>
    /// The swept ceilings, matching the CPU shapes' grid. 352,800 is above the compiled
    /// <see cref="Eip8141Constants.MaxVerifyGas"/>, so it self-ignores on a stock build and needs a run that
    /// raises the constant.
    /// </summary>
    private static readonly ulong[] SweptCeilings = [100_000ul, 236_285ul, 300_000ul, 352_800ul];

    /// <summary>
    /// The cache-residency ladder, coldest last. Each rung names what is cold at the moment a slot is read
    /// for the first time in a timed sample.
    /// </summary>
    /// <remarks>
    /// <c>all-warm</c> replays one slot range for every sample, so every read after the first is served by
    /// the trie store's node cache. It is the in-memory floor and the closest thing here to what the
    /// MemDb-backed harnesses measure.
    ///
    /// <c>cold-node-cache</c> gives each sample its own never-read slot range, so no Nethermind-level cache
    /// can hold it, but the pages were written by this same process moments earlier and the operating
    /// system still has them. This is the page-cache-hit rung.
    ///
    /// <c>cold-page-cache</c> is the same, with <c>posix_fadvise(POSIX_FADV_DONTNEED)</c> applied over the
    /// database directory before each timed sample, so the read cannot be served by the page cache either.
    /// Whether it then reaches a device is a property of the run, not of this rung: it does if the database
    /// sits on a block-backed file system, and it does not on <c>tmpfs</c>, where there is nothing below the
    /// page cache. The row's <c>db_fs</c> and <c>read_bytes</c> say which happened. Linux only.
    /// </remarks>
    private static readonly string[] Rungs = ["all-warm", "cold-node-cache", "cold-page-cache"];

    private ColdSloadTestBlockchain _chain = null!;
    private TempPath _dbDirectory = null!;
    private ulong _ceiling;
    private int _slotsPerTx;

    /// <summary>Slot ordinals reserved per sample, one above the reads a prefix completes so that the
    /// out-of-gas read at the end of one sample cannot warm the next sample's first slot.</summary>
    private int _saltStride;

    /// <summary>Next unused slot range. Rungs draw from one rising sequence so a later rung can never be
    /// served by a range an earlier one already read.</summary>
    private int _nextSalt;

    [SetUp]
    public void Setup()
    {
        _chain = null!;
        _dbDirectory = null!;
    }

    [TearDown]
    public void TearDown()
    {
        _chain?.Dispose();
        _dbDirectory?.Dispose();
    }

    private static IEnumerable<TestCaseData> CeilingCases()
    {
        foreach (ulong ceiling in SweptCeilings) yield return new TestCaseData(ceiling);
    }

    /// <summary>
    /// Measures rejection of a validation prefix that spends its budget on scattered cold storage reads,
    /// once per rung of the cache-residency ladder.
    /// </summary>
    [TestCaseSource(nameof(CeilingCases))]
    public async Task Reject_cost_of_a_cold_sload_prefix(ulong ceiling)
    {
        Eip8141MeasurementGuards.SkipIfCeilingUnreachable(ceiling);
        ColdSloadStorageFixture.SkipUnlessLinux();

        _ceiling = ceiling;
        _slotsPerTx = ColdSloadPrefix.SlotsPerPrefix(ceiling);
        _saltStride = _slotsPerTx + 1;
        _nextSalt = 1;

        // Every cold sample needs a slot range no earlier sample touched, so the fixture seeds one range per
        // submission of the coldest rungs plus the probe and the warm-up.
        int coldRungs = Rungs.Length - 1;
        int coldSamples = (Warmup + Samples) * coldRungs + Warmup + Samples + 1;
        await BuildChain((coldSamples + 1) * _saltStride);

        int observedSloads = ProbeColdSloadCount();
        Assert.That(observedSloads, Is.EqualTo(_slotsPerTx).Within(1),
            $"the prefix completed {observedSloads} storage reads against the {_slotsPerTx} its gas budget was "
            + "sized for, so the code the EVM ran is not the loop this fixture seeded slots for and the "
            + "per-slot figures would be labelled with the wrong count");

        AssertProbeIsRejectedBySimulation();

        foreach (string rung in Rungs) MeasureRung(rung, observedSloads);
    }

    private void MeasureRung(string rung, int sloadsPerTx)
    {
        bool coldSlots = rung != "all-warm";
        bool dropPageCache = rung == "cold-page-cache";

        // Salt 0 is the probe's; warm-up and timed samples take disjoint ranges above it so no timed sample
        // can be served by a range an earlier one already pulled in.
        int salt = _nextSalt;
        for (int i = 0; i < Warmup; i++, salt++) SubmitFrame(coldSlots ? salt : 0, salt);

        if (dropPageCache)
        {
            FlushState();
            StorageResidency.SyncAll();
        }

        long readBytesBefore = StorageResidency.ProcessReadBytes();
        int fadvisedFiles = 0;
        List<double> submitMicros = new(Samples);
        for (int i = 0; i < Samples; i++, salt++)
        {
            if (dropPageCache) fadvisedFiles = StorageResidency.DropPageCache(_dbDirectory.Path);

            Transaction tx = FrameTx(coldSlots ? salt : 0, salt);
            long start = Stopwatch.GetTimestamp();
            AcceptTxResult result = _chain.TxPool.SubmitTx(tx, TxHandlingOptions.None);
            submitMicros.Add(Stopwatch.GetElapsedTime(start).TotalMicroseconds);

            if (result != AcceptTxResult.FrameSimulationFailed)
            {
                Assert.Fail($"{rung} sample {i} was not rejected by the simulation stage: {result}");
            }
        }

        _nextSalt = salt;
        long readBytes = StorageResidency.ProcessReadBytes() - readBytesBefore;
        submitMicros.Sort();
        double p50 = Percentile(submitMicros, 0.50);

        Emit($"case=frame_reject shape=sload-cold storage_backend=rocksdb_trie rung={rung} "
             + $"verify_gas={_ceiling} frame_gas_available={_ceiling} frame_gas_burned={_ceiling} "
             + $"cold_sloads={sloadsPerTx} samples={Samples} "
             + $"submit_p50_us={p50:F1} "
             + $"submit_p90_us={Percentile(submitMicros, 0.90):F1} "
             + $"submit_p99_us={Percentile(submitMicros, 0.99):F1} "
             + $"submit_max_us={submitMicros[^1]:F1} "
             + $"submit_us_per_Mgas={p50 * 1_000_000 / _ceiling:F1} "
             + $"us_per_kgas={p50 * 1_000 / _ceiling:F3} "
             + $"us_per_sload={p50 / sloadsPerTx:F3} "
             + $"us_per_Mgas_basis=offered "
             + $"read_bytes_total={readBytes} read_bytes_per_sample={readBytes / Samples} "
             + $"db_bytes_on_disk={StorageResidency.BytesOnDisk(_dbDirectory.Path)} fadvised_files={fadvisedFiles} "
             + $"db_fs={StorageResidency.FileSystemOf(_dbDirectory.Path)} {_chain.TrieCacheFields} "
             + $"reject_reason=\"FrameSimulationFailed\"");
    }

    /// <summary>Pushes the state database's memtables into files so the page cache is what holds them.</summary>
    private void FlushState()
    {
        _chain.DbProvider.StateDb.Flush();
        _chain.DbProvider.CodeDb.Flush();
    }

    private void AssertProbeIsRejectedBySimulation()
    {
        long failuresBefore = Nethermind.TxPool.Metrics.PendingTransactionsFrameTxSimulationFailed;
        AcceptTxResult result = _chain.TxPool.SubmitTx(FrameTx(0), TxHandlingOptions.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(AcceptTxResult.FrameSimulationFailed),
                $"the probe must be rejected by the simulation stage, not by a cheaper upstream filter (got {result})");
            Assert.That(Nethermind.TxPool.Metrics.PendingTransactionsFrameTxSimulationFailed,
                Is.GreaterThan(failuresBefore), "the simulation-failure counter did not move");
            Assert.That(_chain.TxPool.GetPendingTransactionsCount(), Is.Zero,
                "a rejected frame transaction must not occupy a pool slot");
        }
    }

    private void SubmitFrame(int slotSalt, int uniqueSalt) =>
        _chain.TxPool.SubmitTx(FrameTx(slotSalt, uniqueSalt), TxHandlingOptions.None);

    /// <summary>
    /// Runs one prefix outside the timed path and counts the storage reads it completed, so the per-slot
    /// figures are divided by observed work rather than by an arithmetic expectation.
    /// </summary>
    private int ProbeColdSloadCount()
    {
        SloadCountingTracer tracer = new();
        BlockHeader head = _chain.BlockTree.Head!.Header;

        using IReadOnlyTxProcessorSource source = _chain.ReadOnlyTxProcessingEnvFactory.Create();
        using IReadOnlyTxProcessingScope scope = source.Build(head);
        scope.TransactionProcessor.SetBlockExecutionContext(head);
        scope.TransactionProcessor.Process(FrameTx(0), tracer, ExecutionOptions.FrameValidationPrefixOnly);

        return tracer.Sloads;
    }

    private async Task BuildChain(int slotsToSeed)
    {
        _dbDirectory = TempPath.GetTempDirectory();
        Directory.CreateDirectory(_dbDirectory.Path);

        _chain = await ColdSloadStorageFixture.BuildChain(
            _dbDirectory.Path, _ceiling, Attacker, AttackerBalance, slotsToSeed);

        FlushState();
        ColdSloadStorageFixture.AssertSeededSlotIsVisible(_chain, Attacker, slotsToSeed);
    }

    /// <summary>
    /// Builds a frame transaction whose calldata word selects the slot range the prefix reads, so each
    /// sample can be given slots no earlier sample touched.
    /// </summary>
    private Transaction FrameTx(int slotSalt, int uniqueSalt = 0) =>
        ColdSloadStorageFixture.FrameTx(Attacker, _ceiling, slotSalt * _saltStride, uniqueSalt);

    // Nearest-rank keeps every reported percentile tied to an observed sample.
    private static double Percentile(List<double> sorted, double quantile)
    {
        if (sorted.Count == 0) return double.NaN;
        int rank = (int)Math.Ceiling(quantile * sorted.Count);
        return sorted[Math.Clamp(rank, 1, sorted.Count) - 1];
    }

    private static void Emit(string line)
    {
        string path = Environment.GetEnvironmentVariable("FRAME_STORAGE_IO_OUT")
                      ?? Path.Combine(Path.GetTempPath(), "frame-tx-storage-io.txt");
        string record = $"RESULT {line}";
        TestContext.Out.WriteLine(record);
        File.AppendAllText(path, record + Environment.NewLine);
    }
}
