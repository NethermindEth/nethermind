// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Int256;
using NUnit.Framework;
using static Nethermind.Blockchain.Test.MeasurementEnvironment;
using static Nethermind.Blockchain.Test.MeasurementStatistics;

namespace Nethermind.Blockchain.Test;

/// <summary>
/// Measures what one block-production attempt costs when the EIP-8141 validation prefix it re-executes
/// spends its whole declared budget on scattered <c>SLOAD</c>s in a RocksDB-backed synthetic state database.
/// </summary>
/// <remarks>
/// The campaign's <c>R_max</c> is bound by the producer at every ceiling, and the binding shape so far is
/// signature stuffing. The admission-side storage harness
/// (<see cref="FrameTxStorageIoMeasurement"/>) put a cold-<c>SLOAD</c> prefix above signature stuffing per
/// unit of declared gas, which would move producer <c>R_max</c> if the same ordering held during block
/// production. This fixture compares that ordering in a synthetic RocksDB steady-state replay; it is not a
/// live-node or home-staker <c>R_max</c> measurement.
///
/// The attempt ladder replays immutable block contents and does not model transaction-pool eviction or retry.
/// It is only a cache-sensitivity diagnostic. <see cref="Production_cost_by_shape"/> measures distinct
/// storage accesses once per build and compares their cost with the CPU-bound shapes.
///
/// Every ceiling is swept on every <see cref="StorageArm"/>. Rung labels describe requested cache conditions;
/// process-wide I/O counters do not attribute reads to an individual prefix or establish device access.
///
/// Every row names the environment it ran in rather than the one it asked for: the resolved backend, cache
/// sizes, pruning mode, file system and database size (<see cref="ColdSloadTestBlockchain.ResolvedConfigFields"/>),
/// process-wide read bytes, and the CPU set the process was allowed to run
/// on. The database filesystem is reported for context, but these synthetic fixtures do not represent a fully
/// populated production-node database.
///
/// Rows are appended as <c>RESULT key=value</c> lines to <c>FRAME_FLOOD_OUT</c>, or
/// <c>frame-tx-storage-producer.txt</c> in the temp directory. Run under <c>taskset -c 0</c>, which the
/// harness refuses to run without.
/// </remarks>
[TestFixture]
[Explicit("measurement harness")]
[NonParallelizable]
public class FrameTxStorageProducerMeasurement
{
    /// <summary>
    /// The swept ceilings, matching the CPU shapes' grid. Values above 300,000 are above the compiled
    /// <see cref="Eip8141Constants.MaxVerifyGas"/>, so it self-ignores on a stock build and needs a run that
    /// raises the constant.
    /// </summary>
    private static readonly ulong[] SweptCeilings =
        [100_000ul, 235_800ul, 250_000ul, 300_000ul, 400_000ul, 500_000ul];

    /// <summary>Untimed build attempts used to move tiered-JIT work outside measurement.</summary>
    private const int Warmup = 40;

    /// <summary>
    /// Timed build attempts per row. Far below the CPU harnesses' counts because every cold attempt needs
    /// its own never-read slot range, and the seeded slot count is this fixture's dominant cost.
    /// </summary>
    private const int Samples = 200;

    /// <summary>Build attempts whose cost the per-attempt ladder reports separately.</summary>
    private const int LadderAttempts = 6;

    /// <summary>Independent ladders, each on a fresh rig over a never-read slot range.</summary>
    private const int LadderRepeats = 20;

    /// <summary>A later replay cost within 10% of the first shows a similar timing cost, not per-read I/O.</summary>
    private const double ReplayCostFloor = 0.90;

    private static readonly UInt256 AttackerBalance = 1_000.Ether;

    private static readonly Address SloadAttacker = TestItem.AddressF;
    private static readonly Address KeccakAttacker = TestItem.AddressD;

    /// <summary>The signature-stuffed shape is rejected before the frame loop, so its code never runs; the
    /// account still needs one, and the banned opcode is what the admission harness gives it.</summary>
    private static readonly Address SignatureAttacker = TestItem.AddressE;

    private ColdSloadTestBlockchain _chain = null!;
    private TempPath _dbDirectory = null!;
    private StorageArm _arm = null!;
    private ulong _ceiling;
    private int _slotsPerTx;

    /// <summary>Slot ordinals reserved per attempt, one above the reads a prefix completes so that the
    /// out-of-gas read at the end of one attempt cannot warm the next attempt's first slot.</summary>
    private int _saltStride;

    /// <summary>Next unused slot range. All rows draw from one rising sequence, so a later row can never be
    /// served by a range an earlier one already read.</summary>
    private int _nextRange;

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

    /// <summary>Compares cache sensitivity when a synthetic producer rig replays the same block contents.</summary>
    /// <remarks>This does not model pool eviction or production retries. The real retry behavior is measured by
    /// <see cref="FrameTxProducerRetryMeasurement"/>; this diagnostic contrasts distinct and reused slot ranges.</remarks>
    [TestCaseSource(nameof(ArmCeilingCases))]
    public async Task Synthetic_replay_cache_sensitivity(StorageArm arm, ulong ceiling)
    {
        Eip8141MeasurementGuards.SkipIfCeilingUnreachable(ceiling);
        ColdSloadStorageFixture.SkipUnlessLinux();
        SkipUnlessSingleCore();

        Prepare(arm, ceiling);
        await BuildChain(Warmup + 3 * LadderRepeats + 2);

        // The ladder's first attempt is the one under test, so the JIT must already be warm when it runs.
        using (ProducerRig warmRig = RigOver(DistinctRanges(Warmup))) warmRig.RunFor(TimeSpanOfBuilds(Warmup));

        MeasureLadder("sload-cold", "fresh");
        MeasureLadder("sload-cold", "reused");
        MeasureLadder("keccak-wide", "fresh");
    }

    private void MeasureLadder(string shape, string range)
    {
        bool reused = range == "reused";

        List<double>[] byAttempt = new List<double>[LadderAttempts];
        for (int i = 0; i < LadderAttempts; i++) byAttempt[i] = new List<double>(LadderRepeats);
        long[] readBytesByAttempt = new long[LadderAttempts];

        for (int repeat = 0; repeat < LadderRepeats; repeat++)
        {
            Transaction[] txs = Transactions(shape, 1);

            if (reused)
            {
                using ProducerRig primer = RigOver(txs);
                primer.ProduceOnce();
                primer.ProduceOnce();
            }

            using ProducerRig rig = RigOver(txs);
            for (int attempt = 0; attempt < LadderAttempts; attempt++)
            {
                long readBefore = StorageResidency.ProcessReadBytes();
                byAttempt[attempt].Add(rig.ProduceOnce());
                readBytesByAttempt[attempt] += StorageResidency.ProcessReadBytes() - readBefore;
            }

            Assert.That(rig.FailingExecutions, Is.EqualTo(LadderAttempts),
                $"{shape}/{range} repeat {repeat} ran {rig.FailingExecutions} executions for {LadderAttempts} "
                + "build attempts, so the producer was not re-executing the frame transaction on every "
                + "attempt; every ratio_to_first_attempt below would then read as a cache hit rather than "
                + "as the absent work it is");
        }

        double firstAttempt = Percentile(byAttempt[0], 0.50);
        double secondAttempt = Percentile(byAttempt[1], 0.50);

        for (int attempt = 0; attempt < LadderAttempts; attempt++)
        {
            double p50 = Percentile(byAttempt[attempt], 0.50);
            Emit($"case=producer_replay_ladder shape={shape} range={range} eviction_model=synthetic_replay "
                 + $"storage_backend={_chain.StorageBackend} ceiling={_ceiling} cold_sloads={ColdSloadsFor(shape)} attempt={attempt + 1} "
                 + $"repeats={LadderRepeats} "
                 + $"build_p50_us={p50:F1} build_p90_us={Percentile(byAttempt[attempt], 0.90):F1} "
                 + $"build_min_us={Min(byAttempt[attempt]):F1} "
                 + $"ratio_to_first_attempt={p50 / firstAttempt:F3} "
                 + $"process_read_bytes_total={readBytesByAttempt[attempt]} {_chain.TrieCacheFields} "
                 + $"{_chain.DbFileFields} {RowEnvironment}");
        }

        Emit($"case=producer_replay_summary shape={shape} range={range} eviction_model=synthetic_replay "
             + $"storage_backend={_chain.StorageBackend} ceiling={_ceiling} cold_sloads={ColdSloadsFor(shape)} first_attempt_p50_us={firstAttempt:F1} "
             + $"later_attempt_p50_us={secondAttempt:F1} "
             + $"later_over_first={secondAttempt / firstAttempt:F3} "
             + $"later_cost_ratio_ge_0_90={(secondAttempt >= firstAttempt * ReplayCostFloor ? "yes" : "no")} "
             + $"{_chain.DbFileFields} {_chain.TrieCacheFields} {RowEnvironment}");
    }

    private int ColdSloadsFor(string shape) => shape == "sload-cold" ? _slotsPerTx : 0;

    /// <summary>The resolved settings and CPU affinity every row of this fixture carries after the fields
    /// older rows already had.</summary>
    private string RowEnvironment => $"{_chain.ResolvedConfigFields} {CpuFields}";

    /// <summary>
    /// Prices one production attempt for every ranked shape on one rig, so the storage shape can be placed
    /// against keccak-wide and signature stuffing without carrying CI absolutes onto other hardware.
    /// </summary>
    /// <remarks>
    /// The <c>sload-cold</c> shape appears three times. <c>fixed</c> replays one transaction in the same
    /// synthetic block. <c>distinct</c> places distinct slot ranges in that block; repeated builds then replay
    /// those same ranges. <c>distinct-fadvise</c> also requests page-cache eviction before each build. These
    /// variants compare steady-state costs under requested cache conditions, not fresh pool retries or
    /// verified per-SLOAD device reads.
    /// </remarks>
    [TestCaseSource(nameof(ArmCeilingCases))]
    public async Task Production_cost_by_shape(StorageArm arm, ulong ceiling)
    {
        Eip8141MeasurementGuards.SkipIfCeilingUnreachable(ceiling);
        ColdSloadStorageFixture.SkipUnlessLinux();
        SkipUnlessSingleCore();

        int repeats = StorageRepetition.Repeats;
        List<double> keccak = new(repeats);
        List<double> signature = new(repeats);
        List<double> storageFixed = new(repeats);
        List<double> storageWarm = new(repeats);
        List<double> storageCold = new(repeats);
        long coldReadBytesMin = long.MaxValue;
        string fileSystem = string.Empty;

        // Each repeat seeds its own database, because a repeat that followed another would find the page cache
        // the previous cold rung emptied and its warm rungs would no longer be warm.
        for (int repeat = 1; repeat <= repeats; repeat++)
        {
            Prepare(arm, ceiling);
            await BuildChain(2 * (Warmup + Samples) + Warmup + 2, PaddingSlots);

            keccak.Add(MeasureShape("keccak-wide", "fixed", dropPageCache: false, repeat).UsPerKgas);
            signature.Add(MeasureShape("signature-stuffed", "fixed", dropPageCache: false, repeat).UsPerKgas);
            storageFixed.Add(MeasureShape("sload-cold", "fixed", dropPageCache: false, repeat).UsPerKgas);

            ShapeSample warm = MeasureShape("sload-cold", "distinct", dropPageCache: false, repeat);
            storageWarm.Add(warm.UsPerKgas);

            ShapeSample cold = MeasureShape("sload-cold", "distinct-fadvise", dropPageCache: true, repeat, warm.UsPerKgas);
            storageCold.Add(cold.UsPerKgas);
            coldReadBytesMin = Math.Min(coldReadBytesMin, cold.ReadBytesPerSample);
            fileSystem = cold.FileSystem;

            if (repeat < repeats) DisposeChain();
        }

        double signatureMedian = StorageRepetition.Median(signature);
        double warmMedian = StorageRepetition.Median(storageWarm);
        double coldMedian = StorageRepetition.Median(storageCold);

        Emit($"case=storage_arm_verdict storage_backend={_chain.StorageBackend} ceiling={_ceiling} "
             + $"cold_sloads={_slotsPerTx} repeats={repeats} samples={Samples} "
             + $"keccak_us_per_kgas={StorageRepetition.Median(keccak):F3} "
             + $"signature_us_per_kgas={signatureMedian:F3} "
             + $"storage_fixed_us_per_kgas={StorageRepetition.Median(storageFixed):F3} "
             + $"storage_warm_us_per_kgas={warmMedian:F3} storage_page_cache_drop_us_per_kgas={coldMedian:F3} "
             + $"page_cache_drop_over_signature={coldMedian / signatureMedian:F3} "
             + $"warm_over_signature={warmMedian / signatureMedian:F3} "
             + $"cold_process_read_bytes_per_sample_min={coldReadBytesMin} "
             + $"{ColdRung.Fields(coldReadBytesMin, coldMedian / warmMedian)} "
             + $"{StorageRepetition.SpreadFields("keccak_us_per_kgas", keccak)} "
             + $"{StorageRepetition.SpreadFields("signature_us_per_kgas", signature)} "
             + $"{StorageRepetition.SpreadFields("warm_us_per_kgas", storageWarm)} "
             + $"{StorageRepetition.SpreadFields("page_cache_drop_us_per_kgas", storageCold)} "
             + $"{_chain.DbFileFields} {_chain.TrieCacheFields} {RowEnvironment}");
    }

    /// <summary>What one repeat of one shape cost, and what it read while doing so.</summary>
    private readonly record struct ShapeSample(double UsPerKgas, long ReadBytesPerSample, string FileSystem);

    /// <param name="warmUsPerKgas">The same shape's page-cache-warm cost in this repeat, against which a rung
    /// that claims to be colder is judged. <see cref="double.NaN"/> for rungs that make no such claim.</param>
    private ShapeSample MeasureShape(
        string shape, string rotation, bool dropPageCache, int repeat, double warmUsPerKgas = double.NaN)
    {
        bool distinct = rotation != "fixed";
        int warmupRanges = distinct ? Warmup : 1;
        int sampleRanges = distinct ? Samples : 1;

        using ProducerRig warmRig = RigOver(Transactions(shape, warmupRanges));
        for (int i = 0; i < Warmup; i++) warmRig.ProduceOnce();

        using ProducerRig rig = RigOver(Transactions(shape, sampleRanges));

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
        List<double> micros = new(Samples);
        for (int i = 0; i < Samples; i++)
        {
            if (dropPageCache)
            {
                fadvisedFiles = StorageResidency.DropPageCache(_dbDirectory.Path);
                fadvisedFilesTotal += fadvisedFiles;
            }

            long readBefore = StorageResidency.ProcessReadBytes();
            micros.Add(rig.ProduceOnce());
            readBytes += StorageResidency.ProcessReadBytes() - readBefore;
        }

        Assert.That(rig.FailingExecutions, Is.EqualTo(Samples),
            $"{shape}/{rotation} ran {rig.FailingExecutions} executions for {Samples} build attempts, so the "
            + "producer was not re-executing the frame transaction on every attempt and these rows would "
            + "describe an ordinary block");

        double p50 = Percentile(micros, 0.50);
        double usPerKgas = p50 * 1_000 / _ceiling;
        long readBytesPerSample = readBytes / Samples;
        string fileSystem = StorageResidency.FileSystemOf(_dbDirectory.Path);

        string coldFields = double.IsNaN(warmUsPerKgas)
            ? string.Empty
            : $" {ColdRung.Fields(readBytesPerSample, usPerKgas / warmUsPerKgas)}";

        Emit($"case=production_cost shape={shape} rotation={rotation} storage_backend={_chain.StorageBackend} "
             + $"ceiling={_ceiling} cold_sloads={ColdSloadsFor(shape)} samples={Samples} "
             + $"build_p50_us={p50:F1} build_p90_us={Percentile(micros, 0.90):F1} "
             + $"build_p99_us={Percentile(micros, 0.99):F1} build_min_us={Min(micros):F1} "
             + $"us_per_kgas={usPerKgas:F3} us_per_Mgas_basis=declared "
             + $"process_read_bytes_total={readBytes} process_read_bytes_per_sample={readBytesPerSample} "
             + $"fadvise_calls_accepted={fadvisedFiles} db_bytes_on_disk={StorageResidency.BytesOnDisk(_dbDirectory.Path)} "
             + $"db_fs={fileSystem} {_chain.TrieCacheFields} "
             + $"repeat={repeat} fadvise_calls_accepted_total={fadvisedFilesTotal}{coldFields} {RowEnvironment}");

        return new ShapeSample(usPerKgas, readBytesPerSample, fileSystem);
    }

    private void Prepare(StorageArm arm, ulong ceiling)
    {
        _arm = arm;
        _ceiling = ceiling;
        _slotsPerTx = ColdSloadPrefix.SlotsPerPrefix(ceiling);
        _saltStride = _slotsPerTx + 1;
        _nextRange = 1;
    }

    /// <summary>
    /// Extra seeded slots to make the synthetic database exceed the configured RocksDB block cache. This
    /// creates cache pressure but does not prove that a measured lookup reaches a device.
    /// </summary>
    private const int PaddingSlots = 1_500_000;

    private async Task BuildChain(int rangesNeeded, int paddingSlots = 0)
    {
        _dbDirectory = TempPath.GetTempDirectory();
        Directory.CreateDirectory(_dbDirectory.Path);

        int slotsToSeed = Math.Max((rangesNeeded + 1) * _saltStride, paddingSlots);
        _chain = await ColdSloadStorageFixture.BuildChain(
            _dbDirectory.Path, _ceiling, _arm, SloadAttacker, AttackerBalance, slotsToSeed,
            [(KeccakAttacker, "keccak-wide"), (SignatureAttacker, "banned-opcode")]);

        _chain.PersistState();
        ColdSloadStorageFixture.AssertSeededSlotIsVisible(_chain, SloadAttacker, slotsToSeed);
    }

    private ProducerRig RigOver(IReadOnlyList<Transaction> txs) =>
        ProducerRig.Create(_chain, kRetry: 1, txs, ColdSloadTestBlockchain.BlockGasLimit);

    /// <summary>Cold-<c>SLOAD</c> transactions over <paramref name="count"/> ranges no earlier caller read.</summary>
    private Transaction[] DistinctRanges(int count) => Transactions("sload-cold", count);

    private Transaction[] Transactions(string shape, int count)
    {
        Transaction[] txs = new Transaction[count];
        for (int i = 0; i < count; i++, _nextRange++)
        {
            txs[i] = FrameTxPrefixShapes.FrameTx(
                AttackerFor(shape), shape, _ceiling, _nextRange * _saltStride, _nextRange);
        }
        return txs;
    }

    private static Address AttackerFor(string shape) => shape switch
    {
        "sload-cold" => SloadAttacker,
        "keccak-wide" => KeccakAttacker,
        "signature-stuffed" => SignatureAttacker,
        _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, "unknown prefix shape")
    };

    /// <summary>A window generous enough to cover <paramref name="builds"/> attempts of any swept shape.</summary>
    private static TimeSpan TimeSpanOfBuilds(int builds) => TimeSpan.FromMilliseconds(builds * 20);

    private static double Min(List<double> values)
    {
        double min = double.MaxValue;
        foreach (double value in values)
        {
            if (value < min) min = value;
        }
        return min;
    }

    private static void Emit(string line)
    {
        string path = Environment.GetEnvironmentVariable("FRAME_FLOOD_OUT")
                      ?? Path.Combine(Path.GetTempPath(), "frame-tx-storage-producer.txt");
        string record = $"RESULT {line}";
        TestContext.Out.WriteLine(record);
        File.AppendAllText(path, record + Environment.NewLine);
    }
}
