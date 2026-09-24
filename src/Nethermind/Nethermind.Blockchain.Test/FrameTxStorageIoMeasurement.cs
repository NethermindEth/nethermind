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
using static Nethermind.Blockchain.Test.MeasurementEnvironment;
using static Nethermind.Blockchain.Test.MeasurementStatistics;

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
/// Every ceiling is swept on every <see cref="StorageArm"/>, because the ladder is a property of the caches
/// above the database rather than of the prefix: a rung is only cold while nothing between the prefix and the
/// device is large enough to hold the slot. An arm on which the coldest rung stops reaching the device says
/// so on its rows (<c>cold_rung=COLLAPSED</c>), which is the answer to that question rather than a failure.
///
/// Every row also names the environment it ran in rather than the one it asked for: the resolved backend,
/// cache sizes, pruning mode, file system and database size
/// (<see cref="ColdSloadTestBlockchain.ResolvedConfigFields"/>), and the CPU set the process was allowed to run
/// on. Each repeat seeds a fresh database of one pass's size, so repetition never changes what a rung reads. A run whose temp directory is <c>tmpfs</c> has no block device under it, so no rung can reach one
/// whatever the caches do; a run whose trie cache is smaller than the seeded state has no warm rung.
///
/// Results are appended as <c>RESULT key=value</c> lines to <c>FRAME_STORAGE_IO_OUT</c>, or
/// <c>frame-tx-storage-io.txt</c> in the temp directory. The <c>case=frame_reject</c> rows carry the same
/// keys as <c>FrameTxMempoolDosMeasurement</c>'s, so existing parsing keeps working; the ladder-specific
/// fields are additions.
///
/// Run under <c>taskset -c 0</c>, like the rest of the campaign; the harness refuses to run without it.
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
    private StorageArm _arm = null!;
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
    public void TearDown() => DisposeChain();

    private void DisposeChain()
    {
        _chain?.Dispose();
        _dbDirectory?.Dispose();
        _chain = null!;
        _dbDirectory = null!;
    }

    private static IEnumerable<TestCaseData> ArmCeilingCases()
    {
        foreach (StorageArm arm in StorageArm.Swept())
        {
            foreach (ulong ceiling in SweptCeilings)
            {
                yield return new TestCaseData(arm, ceiling).SetArgDisplayNames(arm.Name, ceiling.ToString());
            }
        }
    }

    /// <summary>
    /// Measures rejection of a validation prefix that spends its budget on scattered cold storage reads,
    /// once per rung of the cache-residency ladder.
    /// </summary>
    [TestCaseSource(nameof(ArmCeilingCases))]
    public async Task Reject_cost_of_a_cold_sload_prefix(StorageArm arm, ulong ceiling)
    {
        Eip8141MeasurementGuards.SkipIfCeilingUnreachable(ceiling);
        ColdSloadStorageFixture.SkipUnlessLinux();
        SkipUnlessSingleCore();

        _arm = arm;
        _ceiling = ceiling;
        _slotsPerTx = ColdSloadPrefix.SlotsPerPrefix(ceiling);
        _saltStride = _slotsPerTx + 1;

        int repeats = StorageRepetition.Repeats;
        Dictionary<string, List<double>> byRung = [];
        foreach (string rung in Rungs) byRung[rung] = new List<double>(repeats);
        long coldestReadBytesMin = long.MaxValue;
        string fileSystem = string.Empty;
        int observedSloads = 0;

        // Each repeat seeds its own database of the size a single pass needs, so repetition does not grow the
        // database a rung reads from and the adversarial arm stays the fixture earlier runs measured.
        for (int repeat = 1; repeat <= repeats; repeat++)
        {
            _nextSalt = 1;

            // Every cold sample needs a slot range no earlier sample touched, so the fixture seeds one range
            // per submission of the coldest rungs plus the probe and the warm-up.
            int coldRungs = Rungs.Length - 1;
            int coldSamples = (Warmup + Samples) * coldRungs + Warmup + Samples + 1;
            await BuildChain((coldSamples + 1) * _saltStride);

            observedSloads = ProbeColdSloadCount();
            Assert.That(observedSloads, Is.EqualTo(_slotsPerTx).Within(1),
                $"the prefix completed {observedSloads} storage reads against the {_slotsPerTx} its gas budget was "
                + "sized for, so the code the EVM ran is not the loop this fixture seeded slots for and the "
                + "per-slot figures would be labelled with the wrong count");

            AssertProbeIsRejectedBySimulation();

            double pageCacheWarm = double.NaN;
            foreach (string rung in Rungs)
            {
                RungSample sample = MeasureRung(rung, observedSloads, repeat, pageCacheWarm);
                byRung[rung].Add(sample.UsPerKgas);

                // The page-cache-hit rung is what the coldest rung has to beat to be a rung at all.
                if (rung == "cold-node-cache") pageCacheWarm = sample.UsPerKgas;
                if (rung == Rungs[^1])
                {
                    coldestReadBytesMin = Math.Min(coldestReadBytesMin, sample.ReadBytesPerSample);
                    fileSystem = sample.FileSystem;
                }
            }

            if (repeat < repeats) DisposeChain();
        }

        double nodeCold = StorageRepetition.Median(byRung["cold-node-cache"]);
        double pageCold = StorageRepetition.Median(byRung[Rungs[^1]]);

        Emit($"case=storage_arm_verdict shape=sload-cold storage_backend={_chain.StorageBackend} "
             + $"verify_gas={_ceiling} cold_sloads={observedSloads} repeats={repeats} samples={Samples} "
             + $"all_warm_us_per_kgas={StorageRepetition.Median(byRung["all-warm"]):F3} "
             + $"cold_node_cache_us_per_kgas={nodeCold:F3} cold_page_cache_us_per_kgas={pageCold:F3} "
             + $"cold_read_bytes_per_sample_min={coldestReadBytesMin} "
             + $"{ColdRung.Fields(coldestReadBytesMin, fileSystem, pageCold / nodeCold)} "
             + $"{StorageRepetition.SpreadFields("all_warm_us_per_kgas", byRung["all-warm"])} "
             + $"{StorageRepetition.SpreadFields("cold_node_cache_us_per_kgas", byRung["cold-node-cache"])} "
             + $"{StorageRepetition.SpreadFields("cold_page_cache_us_per_kgas", byRung[Rungs[^1]])} "
             + $"{_chain.DbFileFields} {_chain.TrieCacheFields} {RowEnvironment}");
    }

    /// <summary>What one repeat of one rung cost, and what it read while doing so.</summary>
    private readonly record struct RungSample(double UsPerKgas, long ReadBytesPerSample, string FileSystem);

    /// <summary>The resolved settings and CPU affinity every row of this fixture carries after the fields
    /// older rows already had.</summary>
    private string RowEnvironment => $"{_chain.ResolvedConfigFields} {CpuFields}";

    /// <param name="pageCacheWarmUsPerKgas">The page-cache-hit rung's cost in this repeat, against which a
    /// rung that claims to be colder is judged. <see cref="double.NaN"/> for rungs that make no such
    /// claim.</param>
    private RungSample MeasureRung(string rung, int sloadsPerTx, int repeat, double pageCacheWarmUsPerKgas)
    {
        bool coldSlots = rung != "all-warm";
        bool dropPageCache = rung == "cold-page-cache";

        // Salt 0 is the probe's; warm-up and timed samples take disjoint ranges above it so no timed sample
        // can be served by a range an earlier one already pulled in.
        int salt = _nextSalt;
        for (int i = 0; i < Warmup; i++, salt++) SubmitFrame(coldSlots ? salt : 0, salt);

        if (dropPageCache)
        {
            _chain.PersistState();
            StorageResidency.SyncAll();
        }

        // Read bytes are bracketed per sample rather than over the whole loop: DropPageCache walks the
        // database directory and opens every file in it, and that walk's own reads would otherwise be
        // reported as reads the prefix performed.
        long readBytes = 0;
        int fadvisedFiles = 0;
        int fadvisedFilesTotal = 0;
        List<double> submitMicros = new(Samples);
        for (int i = 0; i < Samples; i++, salt++)
        {
            if (dropPageCache)
            {
                fadvisedFiles = StorageResidency.DropPageCache(_dbDirectory.Path);
                fadvisedFilesTotal += fadvisedFiles;
            }

            Transaction tx = FrameTx(coldSlots ? salt : 0, salt);
            long readBefore = StorageResidency.ProcessReadBytes();
            long start = Stopwatch.GetTimestamp();
            AcceptTxResult result = _chain.TxPool.SubmitTx(tx, TxHandlingOptions.None);
            submitMicros.Add(Stopwatch.GetElapsedTime(start).TotalMicroseconds);
            readBytes += StorageResidency.ProcessReadBytes() - readBefore;

            if (result != AcceptTxResult.FrameSimulationFailed)
            {
                Assert.Fail($"{rung} sample {i} was not rejected by the simulation stage: {result}");
            }
        }

        _nextSalt = salt;
        submitMicros.Sort();
        double p50 = Percentile(submitMicros, 0.50);
        double usPerKgas = p50 * 1_000 / _ceiling;
        long readBytesPerSample = readBytes / Samples;
        string fileSystem = StorageResidency.FileSystemOf(_dbDirectory.Path);

        string coldFields = double.IsNaN(pageCacheWarmUsPerKgas)
            ? string.Empty
            : $" {ColdRung.Fields(readBytesPerSample, fileSystem, usPerKgas / pageCacheWarmUsPerKgas)}";

        Emit($"case=frame_reject shape=sload-cold storage_backend={_chain.StorageBackend} rung={rung} "
             + $"verify_gas={_ceiling} frame_gas_available={_ceiling} frame_gas_burned={_ceiling} "
             + $"cold_sloads={sloadsPerTx} samples={Samples} "
             + $"submit_p50_us={p50:F1} "
             + $"submit_p90_us={Percentile(submitMicros, 0.90):F1} "
             + $"submit_p99_us={Percentile(submitMicros, 0.99):F1} "
             + $"submit_max_us={submitMicros[^1]:F1} "
             + $"submit_us_per_Mgas={p50 * 1_000_000 / _ceiling:F1} "
             + $"us_per_kgas={usPerKgas:F3} "
             + $"us_per_sload={p50 / sloadsPerTx:F3} "
             + $"us_per_Mgas_basis=offered "
             + $"read_bytes_total={readBytes} read_bytes_per_sample={readBytesPerSample} "
             + $"db_bytes_on_disk={StorageResidency.BytesOnDisk(_dbDirectory.Path)} fadvised_files={fadvisedFiles} "
             + $"db_fs={fileSystem} {_chain.TrieCacheFields} "
             + $"reject_reason=\"FrameSimulationFailed\" "
             + $"repeat={repeat} fadvised_files_total={fadvisedFilesTotal}{coldFields} {RowEnvironment}");

        // Emitted first: an arm that claims a device and did not reach one is a failure worth stopping for,
        // but the row that proves it has to survive the stop.
        if (dropPageCache) ColdRung.AssertReachedDevice(_arm, rung, readBytesPerSample, fileSystem);

        return new RungSample(usPerKgas, readBytesPerSample, fileSystem);
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
            _dbDirectory.Path, _ceiling, _arm, Attacker, AttackerBalance, slotsToSeed);

        _chain.PersistState();
        ColdSloadStorageFixture.AssertSeededSlotIsVisible(_chain, Attacker, slotsToSeed);
    }

    /// <summary>
    /// Builds a frame transaction whose calldata word selects the slot range the prefix reads, so each
    /// sample can be given slots no earlier sample touched.
    /// </summary>
    private Transaction FrameTx(int slotSalt, int uniqueSalt = 0) =>
        ColdSloadStorageFixture.FrameTx(Attacker, _ceiling, slotSalt * _saltStride, uniqueSalt);

    private static void Emit(string line)
    {
        string path = Environment.GetEnvironmentVariable("FRAME_STORAGE_IO_OUT")
                      ?? Path.Combine(Path.GetTempPath(), "frame-tx-storage-io.txt");
        string record = $"RESULT {line}";
        TestContext.Out.WriteLine(record);
        File.AppendAllText(path, record + Environment.NewLine);
    }
}
