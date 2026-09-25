// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain.Tracing;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Container;
using Nethermind.Crypto;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.TxPool;
using NUnit.Framework;

using static Nethermind.Blockchain.Test.MeasurementEnvironment;
using static Nethermind.Blockchain.Test.MeasurementStatistics;

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

    private readonly record struct Groth16Sweep(string Directory, ulong SweepCeiling, ulong FrameGasLimit);

    private static readonly Dictionary<string, Groth16Sweep> Groth16Sweeps = new()
    {
        ["groth16-250k"] = new Groth16Sweep("sweep-250k", 250_000, 250_000),
        ["groth16-300k"] = new Groth16Sweep("sweep-300k", 300_000, 300_000),
        ["groth16-400k"] = new Groth16Sweep("sweep-400k", 400_000, 400_000),
        ["groth16-500k"] = new Groth16Sweep("sweep-500k", 500_000, 500_000),
        ["groth16-soispoke-v2"] = new Groth16Sweep("sweep-soispoke", 235_800, 225_000),
    };

    private static IEnumerable<TestCaseData> AdmissionShapes()
    {
        foreach (string shape in new string[]
                 {
                     "keccak-wide", "groth16-250k", "groth16-300k", "groth16-400k", "groth16-500k",
                     "groth16-soispoke-v2",
                     "signature-stuffed"
                 })
        {
            yield return new TestCaseData(shape);
        }
    }

    private static IEnumerable<TestCaseData> ProductionDelayCases()
    {
        foreach (ulong ceiling in SweptCeilings)
        {
            yield return new TestCaseData(ceiling, 0);
            yield return new TestCaseData(ceiling, 100);
        }
    }

    private static IEnumerable<TestCaseData> CeilingRateCases()
    {
        foreach (ulong ceiling in SweptCeilings)
        {
            foreach (int rate in new int[] { 50, 100, 150, 200 })
            {
                yield return new TestCaseData(ceiling, rate);
            }
        }
    }

    private static IEnumerable<TestCaseData> Groth16RateCases()
    {
        foreach (string shape in new string[]
                 { "groth16-250k", "groth16-300k", "groth16-400k", "groth16-500k", "groth16-soispoke-v2" })
        {
            foreach (int rate in new int[] { 50, 100, 150, 200 })
            {
                yield return new TestCaseData(shape, rate);
            }
        }
    }

    private static IEnumerable<TestCaseData> CeilingCases()
    {
        foreach (ulong ceiling in SweptCeilings)
        {
            yield return new TestCaseData(ceiling);
        }
    }

    private static IEnumerable<TestCaseData> Groth16Cases()
    {
        foreach (string shape in new string[]
                 { "groth16-250k", "groth16-300k", "groth16-400k", "groth16-500k", "groth16-soispoke-v2" })
        {
            yield return new TestCaseData(shape);
        }
    }

    private bool _shedding;

    private byte[] _frameCalldataPrefix = [];

    private TxFrameSignature[] _frameSignatures = [];

    private ulong _frameExecutionGasLimit;

    private FloodTestBlockchain _chain = null!;
    private BlockHeader _parent = null!;
    private Block _workloadBlock = null!;
    private Transaction[] _floodTxs = null!;

    /// <summary>Tracks fresh calldata salts so rejected hashes never bypass simulation through the known cache.</summary>
    private int _saltCursor;

    /// <summary>Environment variable naming the target core count for the analytic core-normalized
    /// projection. Unset (the default) means the projected field is omitted entirely, not zero.</summary>
    private const string ProjectCoresVariable = "FRAME_FLOOD_PROJECT_CORES";

    /// <summary>The plain ceiling sweep shared by the keccak-wide budget-burning and signature-stuffed cases.</summary>
    /// <remarks>235,800 is the current soispoke v2 profile budget; its pool VERIFY frame declares 225,000,
    /// with recent-root and signature costs accounting for the remaining 10,800. The v2 isolated-verifier
    /// arm runs its frame at 225,000 while the measured profile point is 235,800. Values above
    /// <see cref="Eip8141Constants.MaxVerifyGas"/> (300,000) self-ignore unless the workflow raises the
    /// constant. Signature-stuffed transactions are refused before that cap, so they still exercise each
    /// ceiling. <see cref="StuffedSignatureCount"/> floors and reserves frame gas, so a stuffed row may use
    /// up to one fewer signature than the ceiling permits.</remarks>
    private static readonly ulong[] SweptCeilings =
        [100_000ul, 235_800ul, 250_000ul, 300_000ul, 400_000ul, 500_000ul];

    private static readonly int[] AdmissionRates = [50, 100, 150, 200, 250, 300, 350, 400];

    /// <summary>The producer cliff falls inside one 50 tx/s step, so its ramp carries two extra points.</summary>
    private static readonly int[] ProductionRates = [50, 75, 100, 125, 150, 200, 250, 300, 350, 400];

    /// <summary>Maximum drift between the idle baselines bracketing a flood run, for the stable median.</summary>
    private const double MaxBaselineDriftPercent = 5.0;

    /// <summary>Maximum drift for the noisy p99 tail, looser than <see cref="MaxBaselineDriftPercent"/> by
    /// the same 4x margin the hard-fail bounds below use (100% vs 25%): p99 over a few hundred samples
    /// wobbles more than the median before a run is actually unusable, so a tail-only wobble must not flip
    /// valid= on its own.</summary>
    private const double MaxBaselineTailDriftPercent = 20.0;

    private const double BrokenBaselineDriftPercent = 25.0;

    private const double BrokenBaselineTailDriftPercent = 100.0;

    private const double MaxSustainedLagPeriods = 5.0;

    /// <summary>
    /// A non-sustained rate point is re-measured this many times before the ramp accepts the break. F-17:
    /// <see cref="FloodOutcome.MaxLagUs"/> is a running maximum over every submission-lag sample in the
    /// window, so a single scheduling outlier can fail an otherwise-sustained point and forfeit the rest of
    /// the ramp. One retry absorbs a transient outlier without letting the ramp run past a genuine ceiling.
    /// This changes acceptance from one successful measurement to at least one of two, which is one-sided:
    /// a point whose true pass probability is p is accepted with p + (1 - p)p, so a marginal point is
    /// accepted more often than it holds and the reported capacity is biased up by at most one grid step.
    /// The summary carries <c>retries_used</c> and <c>capacity_from_retry</c> so a consumer can reject the
    /// ramps this applies to rather than inferring it.
    /// </summary>
    private const int MaxRatePointRetries = 1;

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
        List<double> ProcessMicros)
    {
        /// <summary>The rate that actually reached the simulator, excluding shed submissions.</summary>
        public double AdmittedRate => Submitted > 0 ? AchievedRate * (Submitted - Shed) / Submitted : 0;
    }

    /// <summary>Single source of truth for shed percentage, so rate-ramp and flood-delay rows agree.</summary>
    private static double ShedPct(FloodOutcome outcome) =>
        outcome.Submitted > 0 ? 100.0 * outcome.Shed / outcome.Submitted : 0;

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
            : Groth16Sweeps.TryGetValue(shape, out Groth16Sweep sweep) ? sweep.SweepCeiling
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
        // Applied regardless of offeredRate: the offeredRate=0 baseline exists to be diffed against the
        // flood arm at the same ceiling, so an unreachable ceiling must skip both together, not leave the
        // baseline behind orphaned once the paired flood row is ignored.
        Eip8141MeasurementGuards.SkipIfCeilingUnreachable(ceiling);
        await BuildChain("keccak-wide", ceiling);

        using ProducerRig rig = ProducerRig.Create(_chain, kRetry: 1, [FrameTx(0, ceiling)], BlockGasLimit);
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

    /// <summary>Open-loop submitter that owns its thread and its cancellation.</summary>
    /// <remarks><see cref="Run{T}"/> is the only way to start it and stops it on every exit path, so the thread
    /// cannot outlive the caller's window however that window ends. It runs once, and the caller that constructed
    /// it disposes it.</remarks>
    internal sealed class FloodGenerator : IDisposable
    {
        private static readonly TimeSpan JoinTimeout = TimeSpan.FromSeconds(30);

        public int Submitted;
        public int Rejected;
        private double _maxLagUs;
        private readonly CancellationTokenSource _cts = new();
        private readonly Thread _thread;
        private bool _started;
        private bool _disposed;

        public double MaxLagUs => Volatile.Read(ref _maxLagUs);

        public bool IsRunning => _started && _thread.IsAlive;

        public FloodGenerator(Func<Transaction, AcceptTxResult> submit, Transaction[] txs, int offeredRate)
        {
            CancellationToken token = _cts.Token;
            _thread = new Thread(() =>
            {
                double ticksPerTx = (double)Stopwatch.Frequency / offeredRate;
                long start = Stopwatch.GetTimestamp();
                for (int i = 0; !token.IsCancellationRequested; i++)
                {
                    long due = start + (long)(i * ticksPerTx);
                    WaitUntil(due, token);
                    if (token.IsCancellationRequested) break;

                    long lag = Stopwatch.GetTimestamp() - due;
                    double lagUs = lag * 1_000_000.0 / Stopwatch.Frequency;
                    if (lagUs > MaxLagUs) Volatile.Write(ref _maxLagUs, lagUs);

                    if (submit(txs[i % txs.Length]) == AcceptTxResult.FrameSimulationFailed) Interlocked.Increment(ref Rejected);
                    Interlocked.Increment(ref Submitted);
                }
            })
            { IsBackground = true, Name = "frame-tx-flood" };
        }

        /// <summary>Runs <paramref name="body"/> with the generator submitting, and stops it on every exit path.</summary>
        /// <remarks>Stopping is not disposal: the caller owns the instance and disposes it.</remarks>
        public T Run<T>(Func<T> body)
        {
            _thread.Start();
            _started = true;
            try
            {
                return body();
            }
            finally
            {
                // Throwing here would mask a failure from the body, but a generator still submitting into
                // infrastructure the caller is about to tear down has to be visible in the run's output.
                if (!Stop()) TestContext.Error.WriteLine("frame-tx flood generator did not stop within the join timeout");
            }
        }

        public void ResetMaxLag() => Volatile.Write(ref _maxLagUs, 0);

        /// <summary>Cancels and joins the submitting thread, returning whether it stopped within the timeout.</summary>
        public bool Stop()
        {
            _cts.Cancel();
            return !_started || _thread.Join(JoinTimeout);
        }

        public void Dispose()
        {
            if (_disposed) return;

            Stop();
            _disposed = true;
            _cts.Dispose();
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
        await MeasureFloodDelay(shape, Groth16Sweeps[shape].SweepCeiling, offeredRate);

    [TestCaseSource(nameof(CeilingRateCases))]
    public async Task Block_processing_delay_under_admission_flood_signature_stuffed(ulong ceiling, int offeredRate) =>
        await MeasureFloodDelay("signature-stuffed", ceiling, offeredRate);

    /// <summary>Pairs each arm above with the node's admission budget left on, pricing the mitigation.</summary>
    [TestCaseSource(nameof(CeilingRateCases))]
    public async Task Block_processing_delay_under_admission_flood_with_shedding(ulong ceiling, int offeredRate) =>
        await MeasureFloodDelay("keccak-wide", ceiling, offeredRate, shedding: true);

    [TestCaseSource(nameof(Groth16RateCases))]
    public async Task Block_processing_delay_under_admission_flood_groth16_with_shedding(string shape, int offeredRate) =>
        await MeasureFloodDelay(shape, Groth16Sweeps[shape].SweepCeiling, offeredRate, shedding: true);

    /// <summary>Signature failures are refused before the simulator, so this arm must shed nothing.</summary>
    [TestCaseSource(nameof(CeilingRateCases))]
    public async Task Block_processing_delay_under_admission_flood_signature_stuffed_with_shedding(ulong ceiling, int offeredRate) =>
        await MeasureFloodDelay("signature-stuffed", ceiling, offeredRate, shedding: true);

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
        // Median and tail get their own soft thresholds (below) rather than one applied via Math.Max to
        // both: the hard-fail bounds already treat them asymmetrically (25% vs 100%), and a noisy p99 must
        // not be able to flip valid=no on its own while the stable median is well inside its bound.
        bool driftValid = baselineDriftPct < MaxBaselineDriftPercent && baselineTailDriftPct < MaxBaselineTailDriftPercent;

        // A generator that fell behind repays the deficit inside the sampled window, which can push the
        // achieved rate above the offered one. The rate floor alone cannot see that; the lag can.
        double lagBudgetUs = offeredRate > 0 ? 1_000_000.0 / offeredRate * MaxSustainedLagPeriods : 0;
        bool lagBounded = offeredRate == 0 || flooded.MaxLagUs <= lagBudgetUs;
        bool saturated = flooded.AchievedRate < offeredRate * RateHeldFloor || !lagBounded;
        double shedPct = ShedPct(flooded);

        // signature-stuffed is refused before the prefix simulator's lock, so unlike execution shapes its
        // rejection throughput scales with cores rather than serializing on contention. That's a claim
        // about achieved_rate, not about this single-core run's delay: with attacker and victim sharing
        // one core (SkipUnlessSingleCore above), w - w0 already reflects that contention, and projecting
        // it by a core count would describe neither the 1-core run nor an N-core one, where the generator
        // and block processor need not share a core at all. Project achieved_rate instead, and only from a
        // run whose baseline was valid and that actually held its offered rate — a noisy or starved run's
        // achieved_rate isn't a throughput this shape could sustain.
        string coreNormalizedField = "";
        if (shape == "signature-stuffed" && IsSingleCore() && driftValid && !saturated
            && int.TryParse(Environment.GetEnvironmentVariable(ProjectCoresVariable), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out int targetCores)
            && targetCores > 0)
        {
            coreNormalizedField = $"achieved_rate_core_normalized={flooded.AchievedRate * targetCores:F1} "
                                  + $"achieved_rate_core_normalized_cores={targetCores} "
                                  + "achieved_rate_core_normalized_basis=analytic_projection_lower_bound ";
        }

        Emit($"case=flood_delay shape={shape} ceiling={ceiling} frame_gas_limit={_frameExecutionGasLimit} shedding={(_shedding ? "on" : "off")} "
             + $"cpus={ObservedCpuSet()} single_core={(IsSingleCore() ? "yes" : "no")} "
             + coreNormalizedField
             + $"W0_after_p50_us={w0After:F1} W0_after_p99_us={w0p99After:F1} "
             + $"baseline_drift_pct={baselineDriftPct:F1} baseline_tail_drift_pct={baselineTailDriftPct:F1} "
             + $"valid={(driftValid ? "yes" : "no")} "
             + $"offered_rate={offeredRate} achieved_rate={flooded.AchievedRate:F1} "
             + $"submitted={flooded.Submitted} rejected={flooded.Rejected} shed={flooded.Shed} shed_pct={shedPct:F1} "
             + $"max_lag_us={flooded.MaxLagUs:F0} lag_budget_us={lagBudgetUs:F0} "
             + $"lag_bounded={(lagBounded ? "yes" : "no")} "
             + $"pending_pool_growth={flooded.PendingPoolGrowth} "
             + $"saturated={(saturated ? "yes" : "no")} "
             + $"delta_per_achieved_tx_per_s_us={(flooded.AdmittedRate > 0 ? (w - w0) / flooded.AdmittedRate : 0):F2} "
             + $"delta_per_admitted_tx_us={(flooded.AdmittedRate > 0 && w > 0 ? (w - w0) * 1_000_000 / (flooded.AdmittedRate * w) : 0):F1} "
             + $"transfers_per_block={TransfersPerBlock} iterations={flooded.ProcessMicros.Count} baseline_count={baseline.Count} "
             + $"W0_p50_us={w0:F1} W0_p95_us={w0p95:F1} "
             + $"W_p50_us={w:F1} W_p95_us={wp95:F1} "
             + $"W0_p99_us={w0p99:F1} W_p99_us={wp99:F1} "
             + $"delta_p50_us={w - w0:F1} delta_p95_us={wp95 - w0p95:F1} delta_p99_us={wp99 - w0p99:F1} "
             + $"delta_p50_pct={(w0 <= 0 ? 0 : (w - w0) / w0 * 100):F1}");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(baselineDriftPct, Is.LessThan(BrokenBaselineDriftPercent),
                $"the two idle baselines' medians disagree by {baselineDriftPct:F1}%, so they describe "
                + "different machine states and no delta can be recovered from them. Re-run on a quieter "
                + $"machine. Rows between {MaxBaselineDriftPercent}% and {BrokenBaselineDriftPercent}% are "
                + "emitted with valid=no instead of failing.");
            Assert.That(baselineTailDriftPct, Is.LessThan(BrokenBaselineTailDriftPercent),
                $"the two idle baselines' p99s disagree by {baselineTailDriftPct:F1}%, past the looser tail "
                + $"bound of {BrokenBaselineTailDriftPercent}% (p99 on a few hundred samples is a noisy, "
                + "near-max statistic, so it tolerates more drift than the median before the run is "
                + "unusable). Tail drift between the median and tail thresholds still counts against "
                + "valid= above without hard-failing.");
            Assert.That(flooded.Rejected + flooded.Shed, Is.EqualTo(flooded.Submitted).Within(1),
                "flood transactions went missing: they were neither simulated nor shed, so this measures an "
                + "idle pool for a reason this harness cannot name");
            Assert.That(baseline, Has.Count.GreaterThan(10),
                "the baseline window collected too few samples for a percentile to mean anything");
            Assert.That(flooded.Submitted, Is.GreaterThan(10),
                "too few transactions landed inside the sampled window for this to be a sustained flood");
            if (shape == "signature-stuffed")
            {
                Assert.That(flooded.Shed, Is.Zero,
                    "a signature refusal is charged before the simulator, so the admission budget must never see it");
            }
        }
    }

    /// <summary>Bounds the sustainable rejection rate by ramping load until the generator falls behind.</summary>
    [TestCaseSource(nameof(CeilingCases))]
    public async Task Sustainable_rejection_rate_by_ramp(ulong ceiling) =>
        await MeasureSustainableRate("keccak-wide", ceiling);

    [TestCaseSource(nameof(Groth16Cases))]
    public async Task Sustainable_rejection_rate_by_ramp_groth16(string shape) =>
        await MeasureSustainableRate(shape, Groth16Sweeps[shape].SweepCeiling);

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

        Func<long>? rejectionCounter = RejectionCounterFor(shape);
        RunRateRamp(ceiling, shape, "rate_ramp", "capacity", extraFields: "", baseline,
            () => MeasureBlockProcessing(MeasureWindow, TimeSpan.Zero),
            rate => MeasureUnderFlood(rate, rejectionCounter));
    }

    private static Func<long>? RejectionCounterFor(string shape) =>
        shape == "signature-stuffed"
            ? () => Nethermind.TxPool.Metrics.PendingTransactionsFrameTxSignatureInvalid
            : null;

    [TestCaseSource(nameof(CeilingCases))]
    public async Task Sustainable_rejection_rate_during_block_production(ulong ceiling) =>
        await MeasureProductionSustainableRate("keccak-wide", ceiling);

    [TestCaseSource(nameof(CeilingCases))]
    public async Task Sustainable_rejection_rate_during_block_production_signature_stuffed(ulong ceiling) =>
        await MeasureProductionSustainableRate("signature-stuffed", ceiling);

    private async Task MeasureProductionSustainableRate(string shape, ulong ceiling)
    {
        SkipUnlessSingleCore();
        if (shape != "signature-stuffed") Eip8141MeasurementGuards.SkipIfCeilingUnreachable(ceiling);
        await BuildChain(shape, ceiling);

        using ProducerRig rig = ProducerRig.Create(_chain, kRetry: 1, [FrameTx(0, ceiling, shape)], BlockGasLimit);
        rig.RunFor(WarmupWindow);
        List<double> baseline = rig.Measure(MeasureWindow);

        Assert.That(rig.FailingExecutions, Is.GreaterThan(0),
            "the producer never re-executed the failing transaction, so this measures an ordinary block");

        Func<long>? rejectionCounter = RejectionCounterFor(shape);
        RunRateRamp(ceiling, shape, "production_rate_ramp", "production_capacity", extraFields: "", baseline,
            () => rig.Measure(MeasureWindow),
            rate => MeasureProductionUnderFlood(rig, rate, rejectionCounter), ProductionRates);
    }

    private void RunRateRamp(
        ulong ceiling, string shape, string rateCase, string summaryCase, string extraFields,
        List<double> baseline, Func<List<double>> measureBaselineAfter,
        Func<int, FloodOutcome> measureAtRate, int[]? rateGrid = null)
    {
        int[] rates = rateGrid ?? AdmissionRates;

        // The fixture warm-up exercises block processing only, so the first flood of a ramp pays the
        // generator's cold start and can miss the lag budget at a rate the node otherwise sustains.
        measureAtRate(rates[0]);

        double w0 = Percentile(baseline, 0.50);
        double w0p99 = Percentile(baseline, 0.99);

        double lastSustained = 0;
        bool sustainedEveryRate = true;
        double firstFailedRate = 0;
        // Rows are held back rather than emitted inline: the drift guard below brackets the whole ramp
        // with a single before/after baseline pair, the same way flood_delay brackets a single flood, so
        // every row in the ramp needs the after-baseline that only exists once the ramp is over. Rows are
        // still flushed below even if the ramp loop or the after-baseline re-measurement throws, and a
        // throw from the latter can no longer erase a ramp failure already caught below (see `failure`).
        // Each row carries its rate point's verdict, resolved once that point's attempts are over: a retried
        // point emits a `sustained=no` row at a rate the ramp went on to accept, so `sustained` alone no
        // longer locates the break. A row whose rate threw mid-attempt keeps `unknown`.
        List<(string Line, string Accepted)> rowLines = [];
        int retriesUsed = 0;
        bool capacityFromRetry = false;

        Exception? failure = null;
        try
        {
            foreach (int rate in rates)
            {
                FloodOutcome outcome = default;
                bool sustained = false;
                int attemptsUsed = 0;
                int firstRowOfRate = rowLines.Count;

                for (int attempt = 1; attempt <= MaxRatePointRetries + 1; attempt++)
                {
                    attemptsUsed = attempt;
                    if (attempt > 1) retriesUsed++;
                    outcome = measureAtRate(rate);

                    double periodUs = 1_000_000.0 / rate;
                    bool rateHeld = outcome.AchievedRate >= rate * RateHeldFloor;
                    bool lagBounded = outcome.MaxLagUs <= periodUs * MaxSustainedLagPeriods;

                    bool pendingPoolStable = outcome.PendingPoolGrowth == 0;
                    sustained = rateHeld && lagBounded;
                    // The plan's no-backlog condition, kept separate from `sustained` above because every
                    // published capacity figure rests on `sustained`'s current meaning.
                    bool sustainedNoBacklog = sustained && pendingPoolStable;
                    double w = Percentile(outcome.ProcessMicros, 0.50);

                    rowLines.Add(($"case={rateCase} shape={shape} ceiling={ceiling} shedding={(_shedding ? "on" : "off")} "
                         + $"{extraFields}cpus={ObservedCpuSet()} single_core={(IsSingleCore() ? "yes" : "no")} offered_rate={rate} "
                         + $"attempt={attempt} "
                         + $"achieved_rate={outcome.AchievedRate:F1} sustained={(sustained ? "yes" : "no")} "
                         + $"sustained_no_backlog={(sustainedNoBacklog ? "yes" : "no")} "
                         + $"max_lag_us={outcome.MaxLagUs:F0} lag_budget_us={periodUs * MaxSustainedLagPeriods:F0} "
                         + $"rate_held={(rateHeld ? "yes" : "no")} lag_bounded={(lagBounded ? "yes" : "no")} "
                         + $"pending_pool_stable={(pendingPoolStable ? "yes" : "no")} "
                         + $"submitted={outcome.Submitted} rejected={outcome.Rejected} shed={outcome.Shed} "
                         + $"shed_pct={ShedPct(outcome):F1} "
                         + $"pending_pool_growth={outcome.PendingPoolGrowth} "
                         + $"W0_p50_us={w0:F1} W_p50_us={w:F1} delta_p50_us={w - w0:F1}", "unknown"));

                    Assert.That(outcome.Rejected + outcome.Shed, Is.EqualTo(outcome.Submitted).Within(1),
                        $"at {rate} tx/s {outcome.Rejected} of {outcome.Submitted} submissions were simulated and "
                        + $"{outcome.Shed} were shed; the rest went missing, so this point measures an idle node for a "
                        + "reason this harness cannot name. A high shed_pct is the node's own admission bound, not a "
                        + "defect: read the capacity it produces as a bound on shedding, not on prefix work.");

                    if (sustained) break;
                }

                for (int i = firstRowOfRate; i < rowLines.Count; i++)
                {
                    rowLines[i] = (rowLines[i].Line, sustained ? "yes" : "no");
                }

                if (sustained)
                {
                    lastSustained = outcome.AchievedRate;
                    capacityFromRetry = attemptsUsed > 1;
                }
                else
                {
                    sustainedEveryRate = false;
                    firstFailedRate = rate;
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        // Bracketing baseline drift guard, mirroring flood_delay's. Measured in its own try so a throw here
        // cannot discard rowLines or replace a ramp failure already caught above.
        string driftFields;
        double baselineDriftPct = double.NaN;
        double baselineTailDriftPct = double.NaN;
        try
        {
            List<double> baselineAfter = measureBaselineAfter();
            double w0After = Percentile(baselineAfter, 0.50);
            double w0p99After = Percentile(baselineAfter, 0.99);
            baselineDriftPct = w0 <= 0 ? 0 : Math.Abs(w0After - w0) / w0 * 100;
            baselineTailDriftPct = w0p99 <= 0 ? 0 : Math.Abs(w0p99After - w0p99) / w0p99 * 100;
            bool driftValid = baselineDriftPct < MaxBaselineDriftPercent && baselineTailDriftPct < MaxBaselineTailDriftPercent;
            driftFields = $"baseline_drift_pct={baselineDriftPct:F1} baseline_tail_drift_pct={baselineTailDriftPct:F1} "
                          + $"valid={(driftValid ? "yes" : "no")}";
        }
        catch (Exception ex)
        {
            driftFields = "baseline_drift_pct=NaN baseline_tail_drift_pct=NaN valid=no";
            if (failure is null)
            {
                failure = ex;
            }
            else
            {
                // The ramp already failed; that diagnosis takes priority over this second, unrelated one,
                // but the second failure must not vanish silently either.
                TestContext.Out.WriteLine(
                    "DEBUG the after-baseline re-measurement also failed while a ramp failure was already "
                    + $"in flight: {ex.GetType().Name}: {ex.Message}");
            }
        }

        foreach ((string line, string accepted) in rowLines) Emit($"{line} accepted={accepted} {driftFields}");

        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();

        bool censored = sustainedEveryRate;

        double capacityUpper = censored ? double.PositiveInfinity : firstFailedRate;

        Emit($"case={summaryCase} shape={shape} ceiling={ceiling} shedding={(_shedding ? "on" : "off")} "
             + $"{extraFields}cpus={ObservedCpuSet()} single_core={(IsSingleCore() ? "yes" : "no")} "
             + $"capacity_sustained_tx_per_s={lastSustained:F1} capacity_lower={lastSustained:F1} "
             + $"capacity_upper={(censored ? "unbounded" : capacityUpper.ToString("F1"))} "
             + $"censored={(censored ? "yes" : "no")} "
             + $"basis=bounded_submission_lag retries_used={retriesUsed} "
             + $"capacity_from_retry={(capacityFromRetry ? "yes" : "no")} "
             + $"max_rate_point_retries={MaxRatePointRetries} note=B_not_fixed {driftFields}");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(baselineDriftPct, Is.LessThan(BrokenBaselineDriftPercent),
                $"the two idle baselines' medians disagree by {baselineDriftPct:F1}%, so the ramp spans "
                + "different machine states and its capacity is unusable");
            Assert.That(baselineTailDriftPct, Is.LessThan(BrokenBaselineTailDriftPercent),
                $"the two idle baselines' p99s disagree by {baselineTailDriftPct:F1}%, so the ramp spans "
                + "different machine states and its capacity is unusable");
            Assert.That(lastSustained, Is.GreaterThan(0),
                "the node sustained none of the offered rates, so the ramp's lowest point is already saturated");
        }
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

    private FloodOutcome MeasureProductionUnderFlood(
        ProducerRig rig, int offeredRate, Func<long>? rejectionCounter = null) =>
        MeasureUnderFloodGeneric(offeredRate,
            warmup: () => { Thread.Sleep(FloodSettle); rig.RunFor(WarmupWindow); },
            measure: rig.Measure,
            rejectionCounter: rejectionCounter,
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

        using FloodGenerator generator = new(
            tx => _chain.TxPool.SubmitTx(tx, TxHandlingOptions.None), _floodTxs, offeredRate);

        return generator.Run(() =>
        {
            warmup();

            int submittedAtStart = Volatile.Read(ref generator.Submitted);
            int rejectedAtStart = Volatile.Read(ref generator.Rejected);
            long rejectionCounterAtStart = rejectionCounter?.Invoke() ?? 0;
            long shedAtStart = ShedCount();
            int pendingAtStart = _chain.TxPool.GetPendingTransactionsCount();

            generator.ResetMaxLag();
            onWindowStart?.Invoke();
            long windowStart = Stopwatch.GetTimestamp();

            List<double> sampleMicros = measure(MeasureWindow);

            long windowEnd = Stopwatch.GetTimestamp();

            // Every counter is read after the join. Reading them while the generator still submits lets a
            // transaction land between two reads and be counted by one but not the other, which breaks the
            // accounting the rows assert on.
            Assert.That(generator.Stop(), Is.True,
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
        });
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
            Frames = [new TxFrame(FrameMode.Verify, FrameFlags.ApproveExecutionAndPayment, target: null, gasLimit: _frameExecutionGasLimit, UInt256.Zero, data)],
            FrameSignatures = _frameSignatures,
            GasLimit = 1_000_000,
            GasPrice = 1.GWei,
            DecodedMaxFeePerGas = 1.GWei,
        };
        tx.Hash = tx.CalculateHash();
        return tx;
    }

    private static Transaction FrameTx(int salt, ulong ceiling, string shape = "keccak-wide")
    {
        byte[] data = new byte[32];
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(28), salt);

        bool stuffed = shape == "signature-stuffed";

        // Block production does not set ExecutionOptions.FrameSignaturesPreValidated, so every attempt
        // re-runs the recoveries. Validation rejects before the frame loop, so the prefix never runs.
        TxFrameSignature[] signatures = stuffed
            ? FrameTxTestFrames.RecoveredSecp256k1Signatures(
                new EthereumEcdsa(TestBlockchainIds.ChainId), FrameTxPrefixShapes.StuffedSignatureCount(ceiling))
            : [];

        Transaction tx = new()
        {
            Type = TxType.FrameTx,
            ChainId = TestBlockchainIds.ChainId,
            Nonce = 0,
            SenderAddress = Attacker,
            Frames = [new TxFrame(FrameMode.Verify, FrameFlags.ApproveExecutionAndPayment, target: null, gasLimit: stuffed ? FrameTxPrefixShapes.MinimalFrameGas : ceiling, UInt256.Zero, data)],
            FrameSignatures = signatures,
            GasLimit = 1_000_000,
            GasPrice = 1.GWei,
            DecodedMaxFeePerGas = 1.GWei,
        };
        tx.Hash = tx.CalculateHash();
        return tx;
    }

    /// <summary>Loads the selected synthetic, signature-stuffed, or Groth16 admission workload.</summary>
    private byte[] LoadAttackCode(string shape, ulong ceiling)
    {
        if (Groth16Sweeps.TryGetValue(shape, out Groth16Sweep sweep))
        {
            byte[] verifierCode = Groth16Artifact(sweep, "verifier.hex");
            _frameCalldataPrefix = Groth16Artifact(sweep, "calldata-invalid.hex");
            _frameSignatures = [];
            _frameExecutionGasLimit = sweep.FrameGasLimit;
            return verifierCode;
        }

        if (shape == "signature-stuffed")
        {
            _frameCalldataPrefix = [];
            _frameSignatures = FrameTxTestFrames.RecoveredSecp256k1Signatures(
                new EthereumEcdsa(TestBlockchainIds.ChainId), FrameTxPrefixShapes.StuffedSignatureCount(ceiling));
            _frameExecutionGasLimit = FrameTxPrefixShapes.MinimalFrameGas;
            return FrameTxPrefixShapes.Code("banned-opcode");
        }

        _frameCalldataPrefix = [];
        _frameSignatures = [];
        _frameExecutionGasLimit = ceiling;
        return FrameTxPrefixShapes.Code(shape);
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

    private static void Emit(string line)
    {
        string path = Environment.GetEnvironmentVariable("FRAME_FLOOD_OUT")
                      ?? Path.Combine(Path.GetTempPath(), "frame-tx-flood.txt");
        // Recorded on every row so a reader of -results.txt alone, without PROVENANCE.txt, can tell whether
        // the build ran against the stock MAX_VERIFY_GAS or one patched by raise_verify_gas_const.
        string record = $"RESULT {line} max_verify_gas_const={Eip8141Constants.MaxVerifyGas}";
        TestContext.Out.WriteLine(record);
        File.AppendAllText(path, record + Environment.NewLine);
    }
}
