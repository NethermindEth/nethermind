// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain.Tracing;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Container;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test;

/// <summary>
/// Measures block-processing delay and sustainable rejection rate while invalid EIP-8141 frame
/// transactions contend with production work on one CPU.
/// </summary>
/// <remarks>
/// Unlike the idle mempool harness, this uses an open-loop admission flood alongside the production block
/// processor. Achieved rate and scheduling lag are reported so saturation cannot look like a cheap flood.
/// Run under <c>taskset -c 0</c>; developer-machine timings are indicative only.
/// </remarks>
[TestFixture]
[Explicit("measurement harness")]
[NonParallelizable]
public class FrameTxFloodMeasurement
{
    private const long BlockGasLimit = 30_000_000;

    /// <summary>Transfer count chosen to keep the baseline CPU-bound and below a full block.</summary>
    private const int TransfersPerBlock = 200;

    private static readonly TimeSpan MeasureWindow = TimeSpan.FromSeconds(3);

    /// <summary>Untimed processing window used to move tiered-JIT work outside measurement.</summary>
    private static readonly TimeSpan WarmupWindow = TimeSpan.FromSeconds(4);

    private static readonly TimeSpan FloodSettle = TimeSpan.FromMilliseconds(750);

    private static readonly TimeSpan FixtureWarmupWindow = TimeSpan.FromSeconds(15);

    private static bool _fixtureWarmed;

    /// <summary>
    /// Distinct flood transactions; this must outlast a run because rejected hashes remain in the
    /// head-scoped known cache.
    /// </summary>
    private const int FloodPoolSize = 4_096;

    private static readonly UInt256 AttackerBalance = 1_000.Ether;

    private static readonly Address Attacker = TestItem.AddressF;

    private static readonly PrivateKey BlockSenderKey = TestItem.PrivateKeyB;

    private static readonly Address TransferTarget = TestItem.AddressC;

    /// <summary>Environment variable containing the externally generated Groth16 artifacts.</summary>
    private const string Groth16ArtifactRootVariable = "FRAME_GROTH16_ARTIFACTS";

    private readonly record struct Groth16Sweep(string Directory, ulong Ceiling);

    private static readonly Dictionary<string, Groth16Sweep> Groth16Sweeps = new()
    {
        ["groth16-236k"] = new Groth16Sweep("sweep-236k", 236_285),
        ["groth16-300k"] = new Groth16Sweep("sweep-300k", 300_000),
        // The result key records the actual ceiling; sweep-500k is the generator's artifact name.
        ["groth16-510k"] = new Groth16Sweep("sweep-500k", 510_000),
        ["groth16-soispoke"] = new Groth16Sweep("sweep-soispoke", 300_000),
    };

    private static IEnumerable<TestCaseData> AdmissionShapes()
    {
        foreach (string shape in new string[]
                 {
                     "keccak-wide", "groth16-236k", "groth16-300k", "groth16-510k", "groth16-soispoke",
                     "signature-stuffed"
                 })
        {
            yield return new TestCaseData(shape);
        }
    }

    private static IEnumerable<TestCaseData> ProductionDelayCases()
    {
        foreach (ulong ceiling in new ulong[] { 100_000ul, 236_285ul, 300_000ul, 500_000ul })
        {
            yield return new TestCaseData(ceiling, 0);
            yield return new TestCaseData(ceiling, 100);
        }
    }

    private static IEnumerable<TestCaseData> CeilingRateCases()
    {
        foreach (ulong ceiling in new ulong[] { 100_000ul, 236_285ul, 300_000ul, 500_000ul })
        {
            foreach (int rate in new int[] { 50, 100, 150, 200 })
            {
                yield return new TestCaseData(ceiling, rate);
            }
        }
    }

    private static IEnumerable<TestCaseData> Groth16RateCases()
    {
        foreach (string shape in new string[] { "groth16-236k", "groth16-300k", "groth16-510k", "groth16-soispoke" })
        {
            foreach (int rate in new int[] { 50, 100, 150, 200 })
            {
                yield return new TestCaseData(shape, rate);
            }
        }
    }

    private static IEnumerable<TestCaseData> CeilingCases()
    {
        foreach (ulong ceiling in new ulong[] { 100_000ul, 236_285ul, 300_000ul, 500_000ul })
        {
            yield return new TestCaseData(ceiling);
        }
    }

    private static IEnumerable<TestCaseData> Groth16Cases()
    {
        foreach (string shape in new string[] { "groth16-236k", "groth16-300k", "groth16-510k", "groth16-soispoke" })
        {
            yield return new TestCaseData(shape);
        }
    }

    private bool _shedding;

    private byte[] _frameCalldataPrefix = [];

    private TxFrameSignature[] _frameSignatures = [];

    private ulong _frameExecutionGasLimit;

    private const ulong MinimalFrameGas = 400;

    private static int StuffedSignatureCount(ulong ceiling) =>
        (int)((ceiling - MinimalFrameGas) / Eip8141Constants.Secp256k1VerificationGasCost);

    private FloodTestBlockchain _chain = null!;
    private BlockHeader _parent = null!;
    private Block _workloadBlock = null!;
    private Transaction[] _floodTxs = null!;

    /// <summary>Tracks fresh calldata salts so rejected hashes never bypass simulation through the known cache.</summary>
    private int _saltCursor;

    /// <summary>Returns the OS-observed CPU set because in-process affinity is unreliable on Linux.</summary>
    private static string ObservedCpuSet()
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                foreach (string line in File.ReadLines("/proc/self/status"))
                {
                    if (line.StartsWith("Cpus_allowed_list:", StringComparison.Ordinal))
                    {
                        return line["Cpus_allowed_list:".Length..].Trim();
                    }
                }
            }

            if (!OperatingSystem.IsWindows()) return "unknown";

            using Process current = Process.GetCurrentProcess();
            return $"mask:{(ulong)(nint)current.ProcessorAffinity:x}";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                       or PlatformNotSupportedException or Win32Exception or InvalidOperationException)
        {
            TestContext.Out.WriteLine($"DEBUG CPU affinity could not be read: {e.GetType().Name}: {e.Message}");
            return "unknown";
        }
    }

    private static bool IsSingleCore()
    {
        string set = ObservedCpuSet();

        if (set.StartsWith("mask:", StringComparison.Ordinal))
        {
            return ulong.TryParse(set["mask:".Length..], NumberStyles.HexNumber, CultureInfo.InvariantCulture,
                       out ulong mask)
                   && BitOperations.PopCount(mask) == 1;
        }

        return set.Length > 0
               && set != "unknown"
               && !set.Contains(',', StringComparison.Ordinal)
               && !set.Contains('-', StringComparison.Ordinal);
    }

    private static void SkipUnlessSingleCore()
    {
        if (IsSingleCore() || Environment.GetEnvironmentVariable("FRAME_FLOOD_ALLOW_MULTICORE") == "1") return;

        Assert.Ignore($"this process may run on CPUs [{ObservedCpuSet()}], so the single-core contention this "
                      + "harness measures does not hold and a flood would appear nearly free. Re-run under "
                      + "`taskset -c 0`, or set FRAME_FLOOD_ALLOW_MULTICORE=1 to measure the uncontended case "
                      + "deliberately.");
    }

    /// <summary>Maximum drift between the idle baselines bracketing a flood run.</summary>
    private const double MaxBaselineDriftPercent = 5.0;

    private const double BrokenBaselineDriftPercent = 25.0;

    private const double MaxSustainedLagPeriods = 5.0;

    private const double RateHeldFloor = 0.95;

    private const double MinDeliveredRateFloor = 0.25;

    private readonly record struct FloodOutcome(
        double OfferedRate,
        double AchievedRate,
        int Submitted,
        int Rejected,
        double MaxLagUs,
        int PendingPoolGrowth,
        int Shed,
        List<double> ProcessMicros);

    [SetUp]
    public void Setup()
    {
        _chain = null!;
        _frameCalldataPrefix = [];
        _frameSignatures = [];
    }

    [TearDown]
    public void TearDown() => _chain?.Dispose();

    /// <summary>Verifies that each flood shape reaches its intended rejection stage before timing.</summary>
    [TestCaseSource(nameof(AdmissionShapes))]
    public async Task Admission_flood_actually_reaches_the_simulator(string shape)
    {
        bool isSignatureStuffed = shape == "signature-stuffed";
        ulong ceiling = isSignatureStuffed ? 500_000
            : Groth16Sweeps.TryGetValue(shape, out Groth16Sweep sweep) ? sweep.Ceiling
            : Eip8141Constants.MaxVerifyGas;
        if (!isSignatureStuffed) Eip8141MeasurementGuards.SkipIfCeilingUnreachable(ceiling);
        await BuildChain(shape, ceiling);

        long simulationFailuresBefore = Volatile.Read(ref Nethermind.TxPool.Metrics.PendingTransactionsFrameTxSimulationFailed);
        long signatureFailuresBefore = Nethermind.TxPool.Metrics.PendingTransactionsFrameTxSignatureInvalid;
        AcceptTxResult result = _chain.TxPool.SubmitTx(FloodFrameTx(0), TxHandlingOptions.None);

        using (Assert.EnterMultipleScope())
        {
            if (isSignatureStuffed)
            {
                Assert.That(result, Is.Not.EqualTo(AcceptTxResult.Accepted),
                    $"the transaction must be refused, not admitted (got {result})");
                Assert.That(Nethermind.TxPool.Metrics.PendingTransactionsFrameTxSignatureInvalid,
                    Is.GreaterThan(signatureFailuresBefore),
                    "the signature-failure counter did not move, so the refusal was not attributed to the signature filter");
            }
            else
            {
                Assert.That(result, Is.EqualTo(AcceptTxResult.FrameSimulationFailed),
                    $"the transaction must be rejected by the simulation stage, not a cheaper filter (got {result})");
                Assert.That(Volatile.Read(ref Nethermind.TxPool.Metrics.PendingTransactionsFrameTxSimulationFailed),
                    Is.GreaterThan(simulationFailuresBefore), "the simulation-failure counter did not move, so the EVM never ran");
            }
            Assert.That(_chain.TxPool.GetPendingTransactionsCount(), Is.Zero,
                "a rejected frame transaction must not occupy a pool slot");
        }

        AssertWorkloadBlockDoesRealWork();
    }

    /// <summary>Measures producer delay while admission competes with a fixed failing-prefix occupancy.</summary>
    [TestCaseSource(nameof(ProductionDelayCases))]
    public async Task Block_production_delay_at_fixed_occupancy(ulong ceiling, int offeredRate)
    {
        SkipUnlessSingleCore();
        if (offeredRate > 0) Eip8141MeasurementGuards.SkipIfCeilingUnreachable(ceiling);
        await BuildChain("keccak-wide", ceiling);

        using ProducerRig rig = ProducerRig.Create(_chain.SpecProvider, kRetry: 1, ceiling: ceiling);
        FloodOutcome outcome = offeredRate > 0
            ? MeasureProductionUnderFlood(rig, offeredRate)
            : NoFloodProductionOutcome(rig);

        double p50 = Percentile(outcome.ProcessMicros, 0.50);
        double p95 = Percentile(outcome.ProcessMicros, 0.95);
        double p99 = Percentile(outcome.ProcessMicros, 0.99);
        bool floodStarved = offeredRate > 0 && outcome.AchievedRate < offeredRate * RateHeldFloor;
        bool delivered = offeredRate == 0 || outcome.AchievedRate > offeredRate * MinDeliveredRateFloor;

        Emit($"case=production_pass_at_fixed_occupancy ceiling={ceiling} offered_rate={offeredRate} "
             + $"shedding={(_shedding ? "on" : "off")} "
             + $"cpus={ObservedCpuSet()} single_core={(IsSingleCore() ? "yes" : "no")} passes={outcome.ProcessMicros.Count} "
             + $"evictions={rig.EvictionsInWindow} failing_executions={rig.ExecutionsInWindow} "
             + $"flood_submitted={outcome.Submitted} flood_rejected={outcome.Rejected} flood_shed={outcome.Shed} "
             + $"flood_achieved_rate={outcome.AchievedRate:F1} flood_starved={(floodStarved ? "yes" : "no")} delivered={(delivered ? "yes" : "no")} "
             + $"production_p50_us={p50:F1} production_p95_us={p95:F1} production_p99_us={p99:F1}");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(rig.FailingExecutions, Is.GreaterThan(0),
                "the producer never re-executed the failing prefix, so this measures an ordinary block");
            Assert.That(rig.Evictions, Is.GreaterThan(0),
                "no eviction fired, so the fixed-occupancy producer workload was not exercised");
            Assert.That(outcome.ProcessMicros, Has.Count.GreaterThan(10),
                "too few production passes for a percentile to mean anything");
            Assert.That(outcome.Rejected + outcome.Shed, Is.EqualTo(outcome.Submitted).Within(1),
                "flood transactions went missing: they were neither simulated nor shed, so the arm measures "
                + "an idle pool for a reason this harness cannot name");
            if (offeredRate > 0)
            {
                Assert.That(outcome.Submitted, Is.GreaterThan(10),
                    "the generator barely ran, so any difference against the no-flood arm is not a flood effect");

                Assert.That(outcome.AchievedRate, Is.GreaterThan(offeredRate * MinDeliveredRateFloor),
                    $"the generator delivered {outcome.AchievedRate:F1} tx/s against {offeredRate} offered, too far "
                    + "below the label for this row to describe a flood at that rate");
            }
        }
    }

    private static FloodOutcome NoFloodProductionOutcome(ProducerRig rig)
    {
        rig.RunFor(WarmupWindow);
        rig.MarkWindowStart();
        return new FloodOutcome(0, 0, 0, 0, 0, 0, 0, rig.Measure(MeasureWindow));
    }

    private sealed class FloodGenerator
    {
        public int Submitted;
        public int Rejected;
        private double _maxLagUs;

        public double MaxLagUs => Volatile.Read(ref _maxLagUs);
        public Thread Thread { get; }

        public FloodGenerator(FloodTestBlockchain chain, Transaction[] txs, int offeredRate, CancellationTokenSource cts) =>
            Thread = new Thread(() =>
            {
                double ticksPerTx = (double)Stopwatch.Frequency / offeredRate;
                long start = Stopwatch.GetTimestamp();
                for (int i = 0; !cts.IsCancellationRequested; i++)
                {
                    long due = start + (long)(i * ticksPerTx);
                    WaitUntil(due, cts.Token);
                    if (cts.IsCancellationRequested) break;

                    long lag = Stopwatch.GetTimestamp() - due;
                    double lagUs = lag * 1_000_000.0 / Stopwatch.Frequency;
                    if (lagUs > MaxLagUs) Volatile.Write(ref _maxLagUs, lagUs);

                    AcceptTxResult result = chain.TxPool.SubmitTx(txs[i % txs.Length], TxHandlingOptions.None);
                    if (result == AcceptTxResult.FrameSimulationFailed) Interlocked.Increment(ref Rejected);
                    Interlocked.Increment(ref Submitted);
                }
            })
            { IsBackground = true, Name = "frame-tx-flood" };

        public void Start() => Thread.Start();

        public void ResetMaxLag() => Volatile.Write(ref _maxLagUs, 0);

        public bool Stop(CancellationTokenSource cts)
        {
            cts.Cancel();
            return Thread.Join(TimeSpan.FromSeconds(30));
        }
    }

    private void AssertWorkloadBlockDoesRealWork()
    {
        BlockReceiptsTracer receiptsTracer = new();
        receiptsTracer.SetOtherTracer(NullBlockTracer.Instance);
        receiptsTracer.StartNewBlockTrace(_workloadBlock);
        _chain.BranchProcessor.Process(_parent, [_workloadBlock], ProcessingOptions.NoValidation, receiptsTracer);
        receiptsTracer.EndBlockTrace();

        long gasUsed = 0;
        int receiptCount = receiptsTracer.TxReceipts.Length;
        foreach (TxReceipt receipt in receiptsTracer.TxReceipts) gasUsed += (long)receipt.GasUsed;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(receiptCount, Is.EqualTo(TransfersPerBlock),
                "the workload block did not execute its transactions, so every W and Δ would be the cost of "
                + "processing an effectively empty block");
            Assert.That(gasUsed, Is.EqualTo((long)TransfersPerBlock * GasCostOf.Transaction),
                "the workload block's transactions did not each consume a plain transfer's gas");
        }

        Emit($"case=workload_block transfers={TransfersPerBlock} receipts={receiptCount} gas_used={gasUsed}");
    }

    /// <summary>Measures block-processing delay across verification ceilings and offered rates.</summary>
    [TestCaseSource(nameof(CeilingRateCases))]
    public async Task Block_processing_delay_under_admission_flood(ulong ceiling, int offeredRate) =>
        await MeasureFloodDelay("keccak-wide", ceiling, offeredRate);

    [TestCaseSource(nameof(Groth16RateCases))]
    public async Task Block_processing_delay_under_admission_flood_groth16(string shape, int offeredRate) =>
        await MeasureFloodDelay(shape, Groth16Sweeps[shape].Ceiling, offeredRate);

    [TestCaseSource(nameof(CeilingRateCases))]
    public async Task Block_processing_delay_under_admission_flood_signature_stuffed(ulong ceiling, int offeredRate) =>
        await MeasureFloodDelay("signature-stuffed", ceiling, offeredRate);

    /// <summary>Pairs the arm above with the node's admission budget left on, pricing the mitigation.</summary>
    [TestCaseSource(nameof(CeilingRateCases))]
    public async Task Block_processing_delay_under_admission_flood_with_shedding(ulong ceiling, int offeredRate) =>
        await MeasureFloodDelay("keccak-wide", ceiling, offeredRate, shedding: true);

    private async Task MeasureFloodDelay(string shape, ulong ceiling, int offeredRate, bool shedding = false)
    {
        SkipUnlessSingleCore();
        if (shape != "signature-stuffed") Eip8141MeasurementGuards.SkipIfCeilingUnreachable(ceiling);
        await BuildChain(shape, ceiling, shedding);

        List<double> baseline = MeasureBlockProcessing(MeasureWindow, WarmupWindow);
        FloodOutcome flooded = MeasureUnderFlood(offeredRate, RejectionCounterFor(shape));

        List<double> baselineAfter = MeasureBlockProcessing(MeasureWindow, TimeSpan.Zero);

        double w0 = Percentile(baseline, 0.50);
        double w = Percentile(flooded.ProcessMicros, 0.50);
        double w0p95 = Percentile(baseline, 0.95);
        double wp95 = Percentile(flooded.ProcessMicros, 0.95);
        double w0p99 = Percentile(baseline, 0.99);
        double wp99 = Percentile(flooded.ProcessMicros, 0.99);
        double w0After = Percentile(baselineAfter, 0.50);
        double w0p99After = Percentile(baselineAfter, 0.99);
        double baselineDriftPct = w0 <= 0 ? 0 : Math.Abs(w0After - w0) / w0 * 100;
        double baselineTailDriftPct = w0p99 <= 0 ? 0 : Math.Abs(w0p99After - w0p99) / w0p99 * 100;
        double worstDriftPct = Math.Max(baselineDriftPct, baselineTailDriftPct);

        // A generator that fell behind repays the deficit inside the sampled window, which can push the
        // achieved rate above the offered one. The rate floor alone cannot see that; the lag can.
        double lagBudgetUs = offeredRate > 0 ? 1_000_000.0 / offeredRate * MaxSustainedLagPeriods : 0;
        bool lagBounded = offeredRate == 0 || flooded.MaxLagUs <= lagBudgetUs;
        bool saturated = flooded.AchievedRate < offeredRate * RateHeldFloor || !lagBounded;

        Emit($"case=flood_delay shape={shape} ceiling={ceiling} shedding={(_shedding ? "on" : "off")} "
             + $"{Groth16FitField(shape)}cpus={ObservedCpuSet()} single_core={(IsSingleCore() ? "yes" : "no")} "
             + $"W0_after_p50_us={w0After:F1} W0_after_p99_us={w0p99After:F1} "
             + $"baseline_drift_pct={baselineDriftPct:F1} baseline_tail_drift_pct={baselineTailDriftPct:F1} "
             + $"valid={(worstDriftPct < MaxBaselineDriftPercent ? "yes" : "no")} "
             + $"offered_rate={offeredRate} achieved_rate={flooded.AchievedRate:F1} "
             + $"submitted={flooded.Submitted} rejected={flooded.Rejected} shed={flooded.Shed} "
             + $"max_lag_us={flooded.MaxLagUs:F0} lag_budget_us={lagBudgetUs:F0} "
             + $"lag_bounded={(lagBounded ? "yes" : "no")} "
             + $"pending_pool_growth={flooded.PendingPoolGrowth} "
             + $"saturated={(saturated ? "yes" : "no")} "
             + $"delta_per_achieved_tx_per_s_us={(flooded.AchievedRate > 0 ? (w - w0) / flooded.AchievedRate : 0):F2} "
             + $"delta_per_admitted_tx_us={(flooded.AchievedRate > 0 && w > 0 ? (w - w0) * 1_000_000 / (flooded.AchievedRate * w) : 0):F1} "
             + $"transfers_per_block={TransfersPerBlock} iterations={flooded.ProcessMicros.Count} "
             + $"W0_p50_us={w0:F1} W0_p95_us={w0p95:F1} "
             + $"W_p50_us={w:F1} W_p95_us={wp95:F1} "
             + $"W0_p99_us={w0p99:F1} W_p99_us={wp99:F1} "
             + $"delta_p50_us={w - w0:F1} delta_p95_us={wp95 - w0p95:F1} delta_p99_us={wp99 - w0p99:F1} "
             + $"delta_p50_pct={(w0 <= 0 ? 0 : (w - w0) / w0 * 100):F1}");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(baselineDriftPct, Is.LessThan(BrokenBaselineDriftPercent),
                $"the two idle baselines disagree by {baselineDriftPct:F1}%, so they describe different machine "
                + "states and no delta can be recovered from them. Re-run on a quieter machine. Rows between "
                + $"{MaxBaselineDriftPercent}% and {BrokenBaselineDriftPercent}% are emitted with valid=no "
                + "instead of failing.");
            Assert.That(flooded.Rejected + flooded.Shed, Is.EqualTo(flooded.Submitted).Within(1),
                "flood transactions went missing: they were neither simulated nor shed, so this measures an "
                + "idle pool for a reason this harness cannot name");
            Assert.That(baseline, Has.Count.GreaterThan(10),
                "the baseline window collected too few samples for a percentile to mean anything");
            Assert.That(flooded.Submitted, Is.GreaterThan(10),
                "too few transactions landed inside the sampled window for this to be a sustained flood");
        }
    }

    /// <summary>Bounds the sustainable rejection rate by ramping load until the generator falls behind.</summary>
    [TestCaseSource(nameof(CeilingCases))]
    public async Task Sustainable_rejection_rate_by_ramp(ulong ceiling) =>
        await MeasureSustainableRate("keccak-wide", ceiling);

    [TestCaseSource(nameof(Groth16Cases))]
    public async Task Sustainable_rejection_rate_by_ramp_groth16(string shape) =>
        await MeasureSustainableRate(shape, Groth16Sweeps[shape].Ceiling);

    [TestCaseSource(nameof(CeilingCases))]
    public async Task Sustainable_rejection_rate_by_ramp_signature_stuffed(ulong ceiling) =>
        await MeasureSustainableRate("signature-stuffed", ceiling);

    private async Task MeasureSustainableRate(string shape, ulong ceiling)
    {
        SkipUnlessSingleCore();
        if (shape != "signature-stuffed") Eip8141MeasurementGuards.SkipIfCeilingUnreachable(ceiling);
        await BuildChain(shape, ceiling);

        RunFor(WarmupWindow);
        List<double> baseline = MeasureBlockProcessing(MeasureWindow, WarmupWindow);
        double w0 = Percentile(baseline, 0.50);

        Func<long>? rejectionCounter = RejectionCounterFor(shape);
        RunRateRamp(ceiling, shape, "rate_ramp", "capacity", Groth16FitField(shape), w0,
            rate => MeasureUnderFlood(rate, rejectionCounter));
    }

    private static Func<long>? RejectionCounterFor(string shape) =>
        shape == "signature-stuffed"
            ? () => Nethermind.TxPool.Metrics.PendingTransactionsFrameTxSignatureInvalid
            : null;

    private static string Groth16FitField(string shape) =>
        shape == "groth16-510k" ? "fits_500k=no " : "";

    [TestCaseSource(nameof(CeilingCases))]
    public async Task Sustainable_rejection_rate_during_block_production(ulong ceiling)
    {
        SkipUnlessSingleCore();
        Eip8141MeasurementGuards.SkipIfCeilingUnreachable(ceiling);
        await BuildChain("keccak-wide", ceiling);

        using ProducerRig rig = ProducerRig.Create(_chain.SpecProvider, kRetry: 1, ceiling: ceiling);
        rig.RunFor(WarmupWindow);
        double w0 = Percentile(rig.Measure(MeasureWindow), 0.50);

        RunRateRamp(ceiling, "keccak-wide", "production_rate_ramp", "production_capacity", extraFields: "", w0,
            rate => MeasureProductionUnderFlood(rig, rate));
    }

    private void RunRateRamp(
        ulong ceiling, string shape, string rateCase, string summaryCase, string extraFields, double w0,
        Func<int, FloodOutcome> measureAtRate)
    {
        int[] rates = [50, 100, 150, 200, 250, 300, 350, 400];

        // The fixture warm-up exercises block processing only, so the first flood of a ramp pays the
        // generator's cold start and can miss the lag budget at a rate the node otherwise sustains.
        measureAtRate(rates[0]);

        double lastSustained = 0;
        bool sustainedEveryRate = true;
        double firstFailedRate = 0;

        foreach (int rate in rates)
        {
            FloodOutcome outcome = measureAtRate(rate);

            double periodUs = 1_000_000.0 / rate;
            bool rateHeld = outcome.AchievedRate >= rate * RateHeldFloor;
            bool lagBounded = outcome.MaxLagUs <= periodUs * MaxSustainedLagPeriods;

            bool pendingPoolStable = outcome.PendingPoolGrowth == 0;
            bool sustained = rateHeld && lagBounded;
            double w = Percentile(outcome.ProcessMicros, 0.50);

            Emit($"case={rateCase} shape={shape} ceiling={ceiling} shedding={(_shedding ? "on" : "off")} "
                 + $"{extraFields}cpus={ObservedCpuSet()} single_core={(IsSingleCore() ? "yes" : "no")} offered_rate={rate} "
                 + $"achieved_rate={outcome.AchievedRate:F1} sustained={(sustained ? "yes" : "no")} "
                 + $"max_lag_us={outcome.MaxLagUs:F0} lag_budget_us={periodUs * MaxSustainedLagPeriods:F0} "
                 + $"rate_held={(rateHeld ? "yes" : "no")} lag_bounded={(lagBounded ? "yes" : "no")} "
                 + $"pending_pool_stable={(pendingPoolStable ? "yes" : "no")} "
                 + $"submitted={outcome.Submitted} rejected={outcome.Rejected} shed={outcome.Shed} "
                 + $"shed_pct={(outcome.Submitted > 0 ? outcome.Shed * 100.0 / outcome.Submitted : 0):F0} "
                 + $"pending_pool_growth={outcome.PendingPoolGrowth} "
                 + $"W0_p50_us={w0:F1} W_p50_us={w:F1} delta_p50_us={w - w0:F1}");

            Assert.That(outcome.Rejected + outcome.Shed, Is.EqualTo(outcome.Submitted).Within(1),
                $"at {rate} tx/s {outcome.Rejected} of {outcome.Submitted} submissions were simulated and "
                + $"{outcome.Shed} were shed; the rest went missing, so this point measures an idle node for a "
                + "reason this harness cannot name. A high shed_pct is the node's own admission bound, not a "
                + "defect: read the capacity it produces as a bound on shedding, not on prefix work.");

            if (sustained)
            {
                lastSustained = outcome.AchievedRate;
            }
            else
            {
                sustainedEveryRate = false;
                firstFailedRate = rate;
                break;
            }
        }

        bool censored = sustainedEveryRate;

        double capacityUpper = censored ? double.PositiveInfinity : firstFailedRate;

        Emit($"case={summaryCase} shape={shape} ceiling={ceiling} shedding={(_shedding ? "on" : "off")} "
             + $"{extraFields}cpus={ObservedCpuSet()} single_core={(IsSingleCore() ? "yes" : "no")} "
             + $"capacity_sustained_tx_per_s={lastSustained:F1} capacity_lower={lastSustained:F1} "
             + $"capacity_upper={(censored ? "unbounded" : capacityUpper.ToString("F1"))} "
             + $"censored={(censored ? "yes" : "no")} "
             + $"basis=bounded_submission_lag note=B_not_fixed");

        Assert.That(lastSustained, Is.GreaterThan(0),
            "the node sustained none of the offered rates, so the ramp's lowest point is already saturated");
    }

    private List<double> MeasureBlockProcessing(TimeSpan window, TimeSpan warmup)
    {
        RunFor(warmup);

        List<double> micros = [];
        long end = Stopwatch.GetTimestamp() + (long)(window.TotalSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < end)
        {
            long start = Stopwatch.GetTimestamp();
            ProcessOnce();
            micros.Add(Stopwatch.GetElapsedTime(start).TotalMicroseconds);
        }
        return micros;
    }

    private void RunFor(TimeSpan window)
    {
        if (window <= TimeSpan.Zero) return;
        long end = Stopwatch.GetTimestamp() + (long)(window.TotalSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < end) ProcessOnce();
    }

    private void ProcessOnce() =>
        _chain.BranchProcessor.Process(_parent, [_workloadBlock], ProcessingOptions.NoValidation, NullBlockTracer.Instance);

    private FloodOutcome MeasureUnderFlood(int offeredRate, Func<long>? rejectionCounter = null) =>
        MeasureUnderFloodGeneric(offeredRate,
            warmup: () => { RunFor(FloodSettle); RunFor(WarmupWindow); },
            measure: window => MeasureBlockProcessing(window, TimeSpan.Zero),
            rejectionCounter);

    private FloodOutcome MeasureProductionUnderFlood(ProducerRig rig, int offeredRate) =>
        MeasureUnderFloodGeneric(offeredRate,
            warmup: () => { Thread.Sleep(FloodSettle); rig.RunFor(WarmupWindow); },
            measure: rig.Measure,
            onWindowStart: rig.MarkWindowStart);

    /// <summary>
    /// Runs an open-loop flood using absolute deadlines, preserving offered load when simulation falls behind.
    /// </summary>
    private FloodOutcome MeasureUnderFloodGeneric(
        int offeredRate, Action warmup, Func<TimeSpan, List<double>> measure, Func<long>? rejectionCounter = null,
        Action? onWindowStart = null)
    {
        _floodTxs = BuildFloodTransactions(_saltCursor);
        _saltCursor += FloodPoolSize;

        using CancellationTokenSource cts = new();
        FloodGenerator generator = new(_chain, _floodTxs, offeredRate, cts);
        generator.Start();

        warmup();

        int submittedAtStart = Volatile.Read(ref generator.Submitted);
        int rejectedAtStart = Volatile.Read(ref generator.Rejected);
        long rejectionCounterAtStart = rejectionCounter?.Invoke() ?? 0;
        long shedAtStart = ShedCount();
        int pendingAtStart = _chain.TxPool.GetPendingTransactionsCount();

        generator.ResetMaxLag();
        onWindowStart?.Invoke();
        long windowStart = Stopwatch.GetTimestamp();

        List<double> sampleMicros;
        try
        {
            sampleMicros = measure(MeasureWindow);
        }
        catch
        {
            generator.Stop(cts);
            throw;
        }

        long windowEnd = Stopwatch.GetTimestamp();

        // Every counter is read after the join. Reading them while the generator still submits lets a
        // transaction land between two reads and be counted by one but not the other, which breaks the
        // accounting the rows assert on.
        Assert.That(generator.Stop(cts), Is.True,
            "the generator did not stop, so its counters would be read while it still writes them");

        int submittedInWindow = generator.Submitted - submittedAtStart;
        int rejectedInWindow = rejectionCounter is null
            ? generator.Rejected - rejectedAtStart
            : (int)(rejectionCounter() - rejectionCounterAtStart);
        int pendingPoolGrowth = _chain.TxPool.GetPendingTransactionsCount() - pendingAtStart;
        int shedInWindow = (int)(ShedCount() - shedAtStart);

        double windowSeconds = (windowEnd - windowStart) / (double)Stopwatch.Frequency;
        double achieved = windowSeconds > 0 ? submittedInWindow / windowSeconds : 0;

        return new FloodOutcome(offeredRate, achieved, submittedInWindow, rejectedInWindow, generator.MaxLagUs,
            pendingPoolGrowth, shedInWindow, sampleMicros);
    }

    /// <summary>
    /// Admission the simulator refused without running the prefix: the per-head budget is spent, or the
    /// simulator is already busy. Shed transactions cost the node nothing, so they are not rejections.
    /// </summary>
    private static long ShedCount() =>
        Volatile.Read(ref Nethermind.TxPool.Metrics.FrameTxSimulationsBudgetExhausted)
        + Volatile.Read(ref Nethermind.TxPool.Metrics.FrameTxSimulationsBusy);

    private static void WaitUntil(long dueTimestamp, CancellationToken token)
    {
        const double SpinThresholdUs = 200;
        while (!token.IsCancellationRequested)
        {
            long remaining = dueTimestamp - Stopwatch.GetTimestamp();
            if (remaining <= 0) return;

            double remainingUs = remaining * 1_000_000.0 / Stopwatch.Frequency;
            if (remainingUs > SpinThresholdUs) Thread.Sleep(1);
            else Thread.Yield();
        }
    }

    /// <summary>
    /// Builds the production-wired pool and block processor, seeding both state views with identical attacker
    /// code because simulation and block processing intentionally use separate world-state scopes.
    /// </summary>
    private async Task BuildChain(string shape, ulong ceiling, bool shedding = false)
    {
        byte[] attackCode = LoadAttackCode(shape, ceiling);

        _shedding = shedding;
        ulong verifyGasCeiling = shape == "signature-stuffed" ? ceiling : 0;
        _chain = await FloodTestBlockchain.CreateFlood(verifyGasCeiling, shedding, builder =>
        {
            builder.AddSingleton<ISpecProvider>(new TestSpecProvider(Eip8141Prototype.Instance));
            builder.AddScoped<IGenesisPostProcessor, IWorldState, ISpecProvider>((worldState, specProvider) =>
                new FunctionalGenesisPostProcessor(_ =>
                {
                    worldState.CreateAccount(Attacker, AttackerBalance);
                    worldState.InsertCode(Attacker, attackCode, specProvider.GenesisSpec);
                    worldState.RecalculateStateRoot();
                }));
        });

        _parent = _chain.BlockTree.Head!.Header;
        _workloadBlock = BuildWorkloadBlock();
        _saltCursor = 0;

        if (!_fixtureWarmed)
        {
            RunFor(FixtureWarmupWindow);
            _fixtureWarmed = true;
        }

        AssertAttackerCodeIsVisible(attackCode);
    }

    private void AssertAttackerCodeIsVisible(byte[] expected)
    {
        IStateReader stateReader = _chain.WorldStateManager.GlobalStateReader;
        Assert.That(stateReader.TryGetAccount(_parent, Attacker, out AccountStruct attacker), Is.True,
            "the attacker account is absent from the chain head");
        byte[]? actual = stateReader.GetCode(attacker.CodeHash);
        Assert.That(actual, Is.EqualTo(expected),
            "the attacker's burn code is not visible at the chain head, so the EVM would run default verify "
            + "code and every number here would describe the wrong work");
    }

    private Block BuildWorkloadBlock()
    {
        Transaction[] transfers = new Transaction[TransfersPerBlock];
        for (int i = 0; i < transfers.Length; i++)
        {
            transfers[i] = Build.A.Transaction
                .WithNonce((ulong)i)
                .WithTo(TransferTarget)
                .WithValue(1.Wei)
                .WithGasLimit(GasCostOf.Transaction)
                .WithGasPrice(1.GWei)
                .SignedAndResolved(BlockSenderKey)
                .TestObject;
        }

        return Build.A.Block
            .WithNumber(_parent.Number + 1)
            .WithParent(_parent)
            .WithGasLimit(BlockGasLimit)
            .WithBaseFeePerGas(UInt256.Zero)
            .WithTransactions(transfers)
            .TestObject;
    }

    private Transaction[] BuildFloodTransactions(int saltBase)
    {
        Transaction[] txs = new Transaction[FloodPoolSize];
        for (int i = 0; i < txs.Length; i++) txs[i] = FloodFrameTx(saltBase + i);
        return txs;
    }

    /// <summary>
    /// Keeps the sender nonce fixed while varying calldata salt, so every rejected sample remains a valid
    /// next-nonce transaction with a distinct hash.
    /// </summary>
    private Transaction FloodFrameTx(int salt)
    {
        byte[] data = new byte[_frameCalldataPrefix.Length + 32];
        _frameCalldataPrefix.CopyTo(data, 0);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(data.Length - 4), salt);

        Transaction tx = new()
        {
            Type = TxType.FrameTx,
            ChainId = TestBlockchainIds.ChainId,
            Nonce = 0,
            SenderAddress = Attacker,
            Frames = [new TxFrame(TxFrame.ModeVerify, TxFrame.ApproveExecutionAndPayment, target: null, gasLimit: _frameExecutionGasLimit, UInt256.Zero, data)],
            FrameSignatures = _frameSignatures,
            GasLimit = 1_000_000,
            GasPrice = 1.GWei,
            DecodedMaxFeePerGas = 1.GWei,
        };
        tx.Hash = tx.CalculateHash();
        return tx;
    }

    private static Transaction FrameTx(int salt, ulong ceiling)
    {
        byte[] data = new byte[32];
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(28), salt);

        Transaction tx = new()
        {
            Type = TxType.FrameTx,
            ChainId = TestBlockchainIds.ChainId,
            Nonce = 0,
            SenderAddress = Attacker,
            Frames = [new TxFrame(TxFrame.ModeVerify, TxFrame.ApproveExecutionAndPayment, target: null, gasLimit: ceiling, UInt256.Zero, data)],
            FrameSignatures = [],
            GasLimit = 1_000_000,
            GasPrice = 1.GWei,
            DecodedMaxFeePerGas = 1.GWei,
        };
        tx.Hash = tx.CalculateHash();
        return tx;
    }

    private static byte[] PrefixCode(string shape) => shape switch
    {
        "keccak-wide" => Prepare.EvmCode
            .Op(Instruction.JUMPDEST)
            .PushData(4096)
            .PushData(0)
            .Op(Instruction.KECCAK256)
            .Op(Instruction.POP)
            .PushData(0)
            .Op(Instruction.JUMP)
            .Done,
        "banned-opcode" => Prepare.EvmCode
            .Op(Instruction.TIMESTAMP)
            .Op(Instruction.POP)
            .Op(Instruction.STOP)
            .Done,
        _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, "unknown prefix shape")
    };

    /// <summary>Loads the selected synthetic, signature-stuffed, or Groth16 admission workload.</summary>
    private byte[] LoadAttackCode(string shape, ulong ceiling)
    {
        if (Groth16Sweeps.TryGetValue(shape, out Groth16Sweep sweep))
        {
            byte[] verifierCode = Groth16Artifact(sweep, "verifier.hex");
            _frameCalldataPrefix = Groth16Artifact(sweep, "calldata-invalid.hex");
            _frameSignatures = [];
            _frameExecutionGasLimit = ceiling;
            return verifierCode;
        }

        if (shape == "signature-stuffed")
        {
            _frameCalldataPrefix = [];
            _frameSignatures = BuildSecp256k1Signatures(StuffedSignatureCount(ceiling));
            _frameExecutionGasLimit = MinimalFrameGas;
            return PrefixCode("banned-opcode");
        }

        _frameCalldataPrefix = [];
        _frameSignatures = [];
        _frameExecutionGasLimit = ceiling;
        return PrefixCode(shape);
    }

    /// <summary>Builds signature entries whose final mismatch occurs only after curve recovery.</summary>
    private static TxFrameSignature[] BuildSecp256k1Signatures(int count)
    {
        EthereumEcdsa ecdsa = new(TestBlockchainIds.ChainId);
        TxFrameSignature[] entries = new TxFrameSignature[count];
        for (int i = 0; i < count; i++)
        {
            byte[] msg = ValueKeccak.Compute(BitConverter.GetBytes(i)).ToByteArray();
            byte[] signed = i == count - 1 ? ValueKeccak.Compute("mismatch"u8).ToByteArray() : msg;
            Signature signature = ecdsa.Sign(TestItem.PrivateKeyA, new Hash256(signed));

            byte[] raw = new byte[TxFrameSignature.Secp256k1SignatureLength];
            raw[0] = signature.RecoveryId;
            signature.RAsSpan.CopyTo(raw.AsSpan(1));
            signature.SAsSpan.CopyTo(raw.AsSpan(33));
            entries[i] = new TxFrameSignature(
                TxFrameSignature.SchemeSecp256k1, TestItem.PrivateKeyA.Address, msg, raw);
        }

        return entries;
    }

    private static byte[] Groth16Artifact(Groth16Sweep sweep, string fileName)
    {
        string root = Groth16ArtifactRoot();
        string path = Path.Combine(root, sweep.Directory, fileName);
        if (!File.Exists(path))
        {
            Assert.Ignore($"Groth16 artifact {path} is missing; build it with the artifacts tree's generate.sh, "
                          + "or point FRAME_GROTH16_ARTIFACTS at a tree that has it.");
        }

        return Bytes.FromHexString(File.ReadAllText(path).Trim());
    }

    /// <summary>Returns the externally generated Groth16 artifact root or skips the privacy cases.</summary>
    private static string Groth16ArtifactRoot()
    {
        string? root = Environment.GetEnvironmentVariable(Groth16ArtifactRootVariable);
        if (string.IsNullOrWhiteSpace(root))
        {
            Assert.Ignore($"{Groth16ArtifactRootVariable} is unset, so the Groth16 sweep artifacts cannot be "
                          + "located. Set it to the generated artifact directory; the privacy workload is the "
                          + "one this campaign exists to price, and skipping it silently is the failure mode "
                          + "that costs the most.");
        }

        return root!;
    }

    private static double Percentile(List<double> values, double quantile)
    {
        if (values.Count == 0) return double.NaN;
        List<double> sorted = [.. values];
        sorted.Sort();
        int rank = (int)Math.Ceiling(quantile * sorted.Count);
        return sorted[Math.Clamp(rank, 1, sorted.Count) - 1];
    }

    /// <summary>Configures the pool's declared-gas precheck for the ceiling being measured.</summary>
    private sealed class FloodTestBlockchain : BasicTestBlockchain
    {
        private ulong _verifyGasCeiling;
        private bool _shedding;

        public static async Task<FloodTestBlockchain> CreateFlood(
            ulong verifyGasCeiling, bool shedding, Action<ContainerBuilder>? configurer = null)
        {
            FloodTestBlockchain chain = new() { _verifyGasCeiling = verifyGasCeiling, _shedding = shedding };
            await chain.Build(configurer);
            return chain;
        }

        protected override IEnumerable<IConfig> CreateConfigs() =>
        [
            new BlocksConfig { MinGasPrice = 0 },
            new TxPoolConfig
            {
                FrameTxMaxVerifyGas = _verifyGasCeiling,
                FrameTxSimulationBudgetPerHeadMs = _shedding ? new TxPoolConfig().FrameTxSimulationBudgetPerHeadMs : int.MaxValue,
            },
        ];
    }

    /// <summary>Runs a never-approving frame transaction through the production transaction executor.</summary>
    private sealed class ProducerRig : IDisposable
    {
        private readonly IDisposable _stateScope;
        private readonly IReleaseSpec _spec;
        private BlockProcessor.BlockProductionTransactionsExecutor _executor = null!;
        private readonly int _kRetry;
        private readonly BlockReceiptsTracer _receiptsTracer = new();
        private readonly Block _block;
        private int _attemptsOnCurrent;
        private CountingAdapter _adapter = null!;

        public int Evictions { get; private set; }

        private int _evictionsAtWindowStart;
        private int _executionsAtWindowStart;

        public int EvictionsInWindow => Evictions - _evictionsAtWindowStart;

        public int ExecutionsInWindow => FailingExecutions - _executionsAtWindowStart;

        public void MarkWindowStart()
        {
            _evictionsAtWindowStart = Evictions;
            _executionsAtWindowStart = FailingExecutions;
        }

        public int FailingExecutions => _adapter.Attempts;

        private ProducerRig(IDisposable stateScope, IReleaseSpec spec, ulong ceiling, int kRetry)
        {
            _stateScope = stateScope;
            _spec = spec;
            _kRetry = kRetry;
            _receiptsTracer.SetOtherTracer(NullBlockTracer.Instance);

            _block = Build.A.Block
                .WithNumber(1)
                .WithBaseFeePerGas(UInt256.Zero)
                .WithBeneficiary(TestItem.AddressE)
                .WithGasLimit(BlockGasLimit)
                .WithTransactions(FrameTx(0, ceiling))
                .TestObject;
        }

        public static ProducerRig Create(ISpecProvider specProvider, int kRetry, ulong ceiling)
        {
            IReleaseSpec spec = specProvider.GenesisSpec;
            IWorldState state = TestWorldStateFactory.CreateForTest();
            IDisposable scope = state.BeginScope(IWorldState.PreGenesis);

            state.CreateAccount(Attacker, AttackerBalance);
            state.InsertCode(Attacker, PrefixCode("keccak-wide"), spec);
            state.Commit(spec);
            state.CommitTree(0);

            EthereumCodeInfoRepository codeInfo = new(state);
            EthereumVirtualMachine vm = new(new TestBlockhashProvider(specProvider), specProvider, LimboLogs.Instance);
            EthereumTransactionProcessor processor = new(
                BlobBaseFeeCalculator.Instance, specProvider, state, vm, codeInfo, LimboLogs.Instance);
            CountingAdapter adapter = new(new BuildUpTransactionProcessorAdapter(processor), measureBurn: false);

            ProducerRig rig = new(scope, spec, ceiling, kRetry);

            IBlockAccessListManager balManager = Substitute.For<IBlockAccessListManager>();
            balManager.Enabled.Returns(false);

            ITxPool gate = Substitute.For<ITxPool>();
            gate.EvictTransaction(Arg.Any<Transaction>()).Returns(_ => rig.OnEvictionRequested());

            rig._adapter = adapter;
            rig._executor = new BlockProcessor.BlockProductionTransactionsExecutor(
                adapter,
                state,
                new BlockProcessor.BlockProductionTransactionPicker(specProvider),
                LimboLogs.Instance,
                balManager,
                gate);

            return rig;
        }

        private bool OnEvictionRequested() => ++_attemptsOnCurrent >= _kRetry;

        public void RunFor(TimeSpan window)
        {
            long end = Stopwatch.GetTimestamp() + (long)(window.TotalSeconds * Stopwatch.Frequency);
            while (Stopwatch.GetTimestamp() < end) ProduceOnce();
        }

        public List<double> Measure(TimeSpan window)
        {
            List<double> micros = [];
            long end = Stopwatch.GetTimestamp() + (long)(window.TotalSeconds * Stopwatch.Frequency);
            while (Stopwatch.GetTimestamp() < end)
            {
                long start = Stopwatch.GetTimestamp();
                ProduceOnce();
                micros.Add(Stopwatch.GetElapsedTime(start).TotalMicroseconds);
            }
            return micros;
        }

        // Resetting the series avoids charging replacement construction differently across K_retry values.
        private void ProduceOnce()
        {
            _receiptsTracer.StartNewBlockTrace(_block);
            _executor.SetBlockExecutionContext(new BlockExecutionContext(_block.Header, _spec));
            _executor.ProcessTransactions(_block, ProcessingOptions.ProducingBlock, _receiptsTracer, CancellationToken.None);
            _receiptsTracer.EndBlockTrace();

            if (_attemptsOnCurrent >= _kRetry)
            {
                Evictions++;
                _attemptsOnCurrent = 0;
            }
        }

        public void Dispose() => _stateScope.Dispose();
    }

    private static void Emit(string line)
    {
        string path = Environment.GetEnvironmentVariable("FRAME_FLOOD_OUT")
                      ?? Path.Combine(Path.GetTempPath(), "frame-tx-flood.txt");
        string record = $"RESULT {line}";
        TestContext.Out.WriteLine(record);
        File.AppendAllText(path, record + Environment.NewLine);
    }
}
