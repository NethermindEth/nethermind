// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Scheduler;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.TxPool;
using NUnit.Framework;

using static Nethermind.Blockchain.Test.MeasurementEnvironment;
using static Nethermind.Blockchain.Test.MeasurementStatistics;

namespace Nethermind.Blockchain.Test;

/// <summary>
/// Ramps the flood on a declared VERIFY gas/s grid shared by every ceiling, so ceilings are compared at equal
/// validation work rather than at equal transaction rates.
/// </summary>
/// <remarks>
/// Three questions the transaction-rate arms cannot answer. The periodic arm routes the flood through the gossip
/// <see cref="BackgroundTaskScheduler"/>, which holds new gossip work while the main branch processor runs, against
/// a victim block sized to a mainnet block's processing time and read on its tail. The fresh-head arm opens each
/// window on a new head, so the per-head simulation budget is as full as it is at the start of a slot.
/// </remarks>
public partial class FrameTxFloodMeasurement
{
    /// <summary>Declared VERIFY gas/s grid, in millions, shared by every ceiling.</summary>
    private static readonly int[] DeclaredGasRateGridMillions = [5, 10, 15, 20, 25, 30, 35, 40, 50, 60];

    private static readonly Address ComputeVictimContract = TestItem.AddressD;

    /// <summary>Calls in the compute victim block, each burning its whole gas limit in the keccak-wide loop.</summary>
    /// <remarks>10M gas of 4 KiB hashing takes tens of milliseconds, the order of a mainnet block's processing
    /// time. It is a time proxy: neither the gas nor the content of a mainnet block.</remarks>
    private const int ComputeVictimCalls = 10;

    private const long ComputeVictimCallGas = 1_000_000;

    /// <summary>Victim cadence: leaves most of each period idle, as a node is between blocks.</summary>
    private static readonly TimeSpan ComputeVictimPeriod = TimeSpan.FromMilliseconds(250);

    private static readonly TimeSpan PeriodicWindow = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan PeriodicWarmup = TimeSpan.FromSeconds(1);

    /// <summary>Back-to-back compute blocks before the first periodic baseline of the process.</summary>
    /// <remarks>The fixture warm-up runs transfer blocks only. Without this, the compute path finishes tiering
    /// during the ramp: on one pinned core the background JIT stalls the victim, and the victim speeds up across
    /// the ramp, so early points read negative deltas.</remarks>
    private static readonly TimeSpan ComputeFixtureWarmup = TimeSpan.FromSeconds(30);

    private static bool _computeWarmed;

    /// <summary>The node's gossip scheduler settings: <c>InitConfig</c> defaults and the scheduler's own deadline.</summary>
    private const int GossipSchedulerConcurrency = 2;

    private const int GossipSchedulerCapacity = 2_048;

    private static readonly TimeSpan GossipTaskTimeout = TimeSpan.FromSeconds(2);

    /// <summary>In-flight handlers can finish on either side of a window edge, on each worker.</summary>
    private const int ScheduledAccountingSlack = 2 * GossipSchedulerConcurrency + 2;

    private const double MinExecutedRatio = 0.95;

    private Block _computeBlock = null!;

    private bool _victimActive;

    private int _missedVictimPeriods;

    private static IEnumerable<TestCaseData> CeilingPathCases()
    {
        foreach (ulong ceiling in FrameTxPrefixShapes.SweptCeilings)
        {
            yield return new TestCaseData(ceiling, "direct");
            yield return new TestCaseData(ceiling, "scheduler");
        }
    }

    private static IEnumerable<TestCaseData> CeilingArmCases()
    {
        foreach (ulong ceiling in FrameTxPrefixShapes.SweptCeilings)
        {
            yield return new TestCaseData(ceiling, "import");
            yield return new TestCaseData(ceiling, "producer");
        }
    }

    /// <summary>A1: the v2 import victim, ramped on declared gas/s.</summary>
    [TestCaseSource(nameof(CeilingCases))]
    public async Task Sustainable_declared_gas_rate_by_ramp(ulong ceiling) =>
        await MeasureImportGasRamp("keccak-wide", ceiling);

    [TestCaseSource(nameof(CeilingCases))]
    public async Task Sustainable_declared_gas_rate_by_ramp_signature_stuffed(ulong ceiling) =>
        await MeasureImportGasRamp("signature-stuffed", ceiling);

    /// <summary>A2: the v2 producer victim, ramped on declared gas/s.</summary>
    [TestCaseSource(nameof(CeilingCases))]
    public async Task Sustainable_declared_gas_rate_during_block_production(ulong ceiling) =>
        await MeasureProductionGasRamp("keccak-wide", ceiling);

    [TestCaseSource(nameof(CeilingCases))]
    public async Task Sustainable_declared_gas_rate_during_block_production_signature_stuffed(ulong ceiling) =>
        await MeasureProductionGasRamp("signature-stuffed", ceiling);

    /// <summary>A3: a periodic compute victim, with the flood submitted directly or through the gossip scheduler.</summary>
    [TestCaseSource(nameof(CeilingPathCases))]
    public async Task Periodic_import_delay_by_declared_gas_rate(ulong ceiling, string path) =>
        await MeasurePeriodicImportGasRamp("keccak-wide", ceiling, path);

    [TestCaseSource(nameof(CeilingPathCases))]
    public async Task Periodic_import_delay_by_declared_gas_rate_signature_stuffed(ulong ceiling, string path) =>
        await MeasurePeriodicImportGasRamp("signature-stuffed", ceiling, path);

    /// <summary>A4: shedding on, each window opening on a fresh head.</summary>
    /// <remarks>keccak-wide only: signature refusals never reach the simulator's budget.</remarks>
    [TestCaseSource(nameof(CeilingArmCases))]
    public async Task Fresh_head_declared_gas_rate_with_shedding(ulong ceiling, string arm) =>
        await MeasureFreshHeadGasRamp(ceiling, arm);

    private async Task MeasureImportGasRamp(string shape, ulong ceiling)
    {
        SkipUnlessSingleCore();
        if (shape != "signature-stuffed") Eip8141MeasurementGuards.SkipIfCeilingUnreachable(ceiling);
        await BuildChain(shape, ceiling);

        RunFor(WarmupWindow);
        List<double> baseline = MeasureBlockProcessing(MeasureWindow, WarmupWindow);
        Func<long>? rejectionCounter = RejectionCounterFor(shape);
        await using GossipSubmitter submitter = new(_chain, scheduled: false, static () => false);

        RunGasRamp(ceiling, shape, "gas_rate_ramp", "gas_capacity", "gossip_path=direct victim=transfer_block ", baseline,
            () => MeasureBlockProcessing(MeasureWindow, TimeSpan.Zero),
            rate => DirectPoint(MeasureAccounted(submitter, RefusalCounterFor(shape), () => MeasureUnderFloodGeneric(rate,
                warmup: () => { RunFor(FloodSettle); RunFor(WarmupWindow); },
                measure: window => MeasureBlockProcessing(window, TimeSpan.Zero),
                rejectionCounter, submit: submitter.Submit,
                poolSize: PoolSizeFor(rate, FloodSettle + WarmupWindow + MeasureWindow)))));
    }

    private async Task MeasureProductionGasRamp(string shape, ulong ceiling)
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
        await using GossipSubmitter submitter = new(_chain, scheduled: false, static () => false);
        RunGasRamp(ceiling, shape, "production_gas_rate_ramp", "production_gas_capacity", "gossip_path=direct victim=producer_pass ",
            baseline, () => rig.Measure(MeasureWindow),
            rate => DirectPoint(MeasureAccounted(submitter, RefusalCounterFor(shape), () => MeasureUnderFloodGeneric(rate,
                warmup: () => { Thread.Sleep(FloodSettle); rig.RunFor(WarmupWindow); },
                measure: rig.Measure, rejectionCounter, onWindowStart: rig.MarkWindowStart, submit: submitter.Submit,
                poolSize: PoolSizeFor(rate, FloodSettle + WarmupWindow + MeasureWindow)))));
    }

    private async Task MeasurePeriodicImportGasRamp(string shape, ulong ceiling, string path)
    {
        SkipUnlessSingleCore();
        if (shape != "signature-stuffed") Eip8141MeasurementGuards.SkipIfCeilingUnreachable(ceiling);
        await BuildChain(shape, ceiling, computeVictim: true);
        AssertComputeBlockDoesRealWork();
        if (!_computeWarmed)
        {
            long warmEnd = Stopwatch.GetTimestamp() + (long)(ComputeFixtureWarmup.TotalSeconds * Stopwatch.Frequency);
            while (Stopwatch.GetTimestamp() < warmEnd)
            {
                _chain.BranchProcessor.Process(_parent, [_computeBlock], ProcessingOptions.NoValidation, NullBlockTracer.Instance);
            }
            _computeWarmed = true;
        }

        bool scheduled = path == "scheduler";
        await using GossipSubmitter submitter = new(_chain, scheduled, () => Volatile.Read(ref _victimActive));

        MeasurePeriodicComputeBlocks(PeriodicWindow);
        List<double> baseline = MeasurePeriodicComputeBlocks(PeriodicWindow);

        // The scheduled path returns before the pool sees the transaction, so rejections come from the pool's own
        // counters on both paths.
        Func<long> rejectionCounter = shape == "signature-stuffed"
            ? () => Nethermind.TxPool.Metrics.PendingTransactionsFrameTxSignatureInvalid
            : () => Volatile.Read(ref Nethermind.TxPool.Metrics.PendingTransactionsFrameTxSimulationFailed);

        RunGasRamp(ceiling, shape, "periodic_import_gas_ramp", "periodic_import_gas_capacity",
            $"gossip_path={path} victim=compute_block victim_calls={ComputeVictimCalls} "
            + $"victim_gas={ComputeVictimCalls * ComputeVictimCallGas} victim_period_ms={ComputeVictimPeriod.TotalMilliseconds:F0} ",
            baseline, () => MeasurePeriodicComputeBlocks(PeriodicWindow),
            rate =>
            {
                GossipSubmitter.Counts atStart = default, atEnd = default;
                int missedAtStart = 0, gen2AtStart = 0, gen2AtEnd = 0;
                FloodOutcome outcome = MeasureAccounted(submitter, RefusalCounterFor(shape), () => MeasureUnderFloodGeneric(rate,
                    warmup: () => { Thread.Sleep(FloodSettle); MeasurePeriodicComputeBlocks(PeriodicWarmup); },
                    measure: MeasurePeriodicComputeBlocks, rejectionCounter,
                    onWindowStart: () => { atStart = submitter.Read(); missedAtStart = _missedVictimPeriods; gen2AtStart = GC.CollectionCount(2); },
                    submit: submitter.Submit, window: PeriodicWindow,
                    poolSize: PoolSizeFor(rate, FloodSettle + PeriodicWarmup + PeriodicWindow),
                    onWindowEnd: () => { atEnd = submitter.Read(); gen2AtEnd = GC.CollectionCount(2); }));

                GossipSubmitter.Counts window = atEnd - atStart;
                double executedRatio = outcome.Submitted > 0 ? (double)window.Executed / outcome.Submitted : 0;
                double duringVictimPct = window.Starts > 0 ? 100.0 * window.StartsDuringVictim / window.Starts : 0;
                bool drained = !scheduled || (executedRatio >= MinExecutedRatio && window.Dropped == 0 && window.Refused == 0);

                return new GasPoint(outcome, drained,
                    $"scheduled={window.Scheduled} executed={window.Executed} dropped={window.Dropped} refused={window.Refused} "
                    + $"executed_ratio={executedRatio:F3} handler_starts={window.Starts} "
                    + $"handler_starts_during_victim={window.StartsDuringVictim} handler_starts_during_victim_pct={duringVictimPct:F1} "
                    + $"missed_victim_periods={_missedVictimPeriods - missedAtStart} gc_gen2={gen2AtEnd - gen2AtStart} ");
            });
    }

    private async Task MeasureFreshHeadGasRamp(ulong ceiling, string arm)
    {
        SkipUnlessSingleCore();
        Eip8141MeasurementGuards.SkipIfCeilingUnreachable(ceiling);
        await BuildChain("keccak-wide", ceiling, shedding: true);

        string extra = $"arm={arm} gossip_path=direct victim={(arm == "import" ? "transfer_block" : "producer_pass")} window_opens=fresh_head ";
        await using GossipSubmitter submitter = new(_chain, scheduled: false, static () => false);
        Func<long> refusals = RefusalCounterFor("keccak-wide");
        if (arm == "import")
        {
            RunFor(WarmupWindow);
            List<double> baseline = MeasureBlockProcessing(MeasureWindow, WarmupWindow);
            RunGasRamp(ceiling, "keccak-wide", "fresh_head_gas_ramp", "fresh_head_gas_capacity", extra, baseline,
                () => MeasureBlockProcessing(MeasureWindow, TimeSpan.Zero),
                rate => DirectPoint(MeasureAccounted(submitter, refusals, () => MeasureUnderFloodGeneric(rate,
                    warmup: () => RunFor(FloodSettle),
                    measure: window => MeasureBlockProcessing(window, TimeSpan.Zero), submit: submitter.Submit,
                    beforeWindow: AdvanceHead, poolSize: PoolSizeFor(rate, FloodSettle + MeasureWindow)))));
            return;
        }

        using ProducerRig rig = ProducerRig.Create(_chain, kRetry: 1, [FrameTx(0, ceiling)], BlockGasLimit);
        rig.RunFor(WarmupWindow);
        List<double> producerBaseline = rig.Measure(MeasureWindow);
        RunGasRamp(ceiling, "keccak-wide", "fresh_head_gas_ramp", "fresh_head_gas_capacity", extra, producerBaseline,
            () => rig.Measure(MeasureWindow),
            rate => DirectPoint(MeasureAccounted(submitter, refusals, () => MeasureUnderFloodGeneric(rate,
                warmup: () => Thread.Sleep(FloodSettle),
                measure: rig.Measure, onWindowStart: rig.MarkWindowStart, submit: submitter.Submit,
                beforeWindow: AdvanceHead, poolSize: PoolSizeFor(rate, FloodSettle + MeasureWindow)))));
    }

    /// <summary>One ramp point: the flood outcome, whether any arm-specific condition held, and that condition's fields.</summary>
    private readonly record struct GasPoint(FloodOutcome Outcome, bool ArmSustained, string Fields);

    private static GasPoint DirectPoint(FloodOutcome outcome) => new(outcome, true, "");

    /// <summary>Pool refusals a flood caused: the shape's rejection counter plus admission sheds.</summary>
    private static Func<long> RefusalCounterFor(string shape) => shape == "signature-stuffed"
        ? () => Nethermind.TxPool.Metrics.PendingTransactionsFrameTxSignatureInvalid + ShedCount()
        : () => Volatile.Read(ref Nethermind.TxPool.Metrics.PendingTransactionsFrameTxSimulationFailed) + ShedCount();

    /// <summary>Runs one point and checks, over the whole point, that the pool refused or shed every submission.</summary>
    /// <remarks>A window's counters are read while the generator still submits, so at hundreds of tx/s they can
    /// disagree by a few transactions without any going missing. Once the generator has joined and the scheduler has
    /// drained, nothing writes them.</remarks>
    private static FloodOutcome MeasureAccounted(GossipSubmitter submitter, Func<long> refusals, Func<FloodOutcome> measure)
    {
        long refusedBefore = refusals();
        int executedBefore = submitter.Read().Executed;
        FloodOutcome outcome = measure();
        Assert.That(submitter.WaitIdle(TimeSpan.FromSeconds(10)), Is.True,
            "the gossip scheduler did not drain after the window, so the next point would inherit its backlog");

        long refused = refusals() - refusedBefore;
        int executed = submitter.Read().Executed - executedBefore;
        Assert.That(Math.Abs(refused - executed), Is.LessThanOrEqualTo(submitter.IsScheduled ? ScheduledAccountingSlack : 1),
            $"the pool refused or shed {refused} of {executed} submissions in this point; the rest went missing");
        return outcome;
    }

    /// <summary>Ramps the declared gas/s grid until a point is not sustained, as <see cref="RunRateRamp"/> does
    /// on the transaction-rate grid, and emits rows under their own case names.</summary>
    /// <remarks>Rows carry p95, p99 and max beside p50, because the periodic arm is read on its tail.</remarks>
    private void RunGasRamp(
        ulong ceiling, string shape, string rateCase, string summaryCase, string extraFields,
        List<double> baseline, Func<List<double>> measureBaselineAfter, Func<int, GasPoint> measureAtRate)
    {
        ulong declaredGasPerTx = FrameTxValidation.ValidationWorkGas(FloodFrameTx(0));
        List<(int Rate, long TargetGasPerSecond)> grid = GasGrid(declaredGasPerTx);

        // The first flood of a ramp pays the generator's cold start, as in the transaction-rate ramps.
        measureAtRate(grid[0].Rate);

        double w0 = Percentile(baseline, 0.50);
        double w0p95 = Percentile(baseline, 0.95);
        double w0p99 = Percentile(baseline, 0.99);
        double w0max = Percentile(baseline, 1.0);

        long lastSustainedGas = 0;
        bool sustainedEveryPoint = true;
        long firstFailedGas = 0;
        List<(string Line, string Accepted)> rowLines = [];
        Exception? failure = null;
        try
        {
            foreach ((int rate, long targetGas) in grid)
            {
                bool firstAttemptSustained = false;
                int firstRowOfRate = rowLines.Count;
                for (int attempt = 1; attempt <= MaxRatePointRetries + 1; attempt++)
                {
                    GasPoint point = measureAtRate(rate);
                    FloodOutcome outcome = point.Outcome;
                    double periodUs = 1_000_000.0 / rate;
                    bool rateHeld = outcome.AchievedRate >= rate * RateHeldFloor;
                    bool lagBounded = outcome.MaxLagUs <= periodUs * MaxSustainedLagPeriods;
                    bool sustained = rateHeld && lagBounded && point.ArmSustained;
                    if (attempt == 1) firstAttemptSustained = sustained;

                    double w = Percentile(outcome.ProcessMicros, 0.50);
                    double wp95 = Percentile(outcome.ProcessMicros, 0.95);
                    double wp99 = Percentile(outcome.ProcessMicros, 0.99);
                    double wmax = Percentile(outcome.ProcessMicros, 1.0);
                    double victimThroughputRatio = baseline.Count > 0 ? (double)outcome.ProcessMicros.Count / baseline.Count : 0;

                    rowLines.Add(($"case={rateCase} shape={shape} ceiling={ceiling} shedding={(_shedding ? "on" : "off")} "
                         + $"{extraFields}cpus={ObservedCpuSet()} single_core={(IsSingleCore() ? "yes" : "no")} "
                         + $"target_declared_gas_per_s={targetGas} declared_gas_per_tx={declaredGasPerTx} "
                         + $"offered_rate={rate} offered_declared_gas_per_s={rate * (double)declaredGasPerTx:F0} attempt={attempt} "
                         + $"achieved_rate={outcome.AchievedRate:F1} sustained={(sustained ? "yes" : "no")} "
                         + $"rate_held={(rateHeld ? "yes" : "no")} lag_bounded={(lagBounded ? "yes" : "no")} "
                         + $"arm_sustained={(point.ArmSustained ? "yes" : "no")} "
                         + $"max_lag_us={outcome.MaxLagUs:F0} lag_budget_us={periodUs * MaxSustainedLagPeriods:F0} "
                         + $"submitted={outcome.Submitted} rejected={outcome.Rejected} shed={outcome.Shed} shed_pct={ShedPct(outcome):F1} "
                         + $"pending_pool_growth={outcome.PendingPoolGrowth} {point.Fields}"
                         + $"W0_p50_us={w0:F1} W_p50_us={w:F1} delta_p50_us={w - w0:F1} "
                         + $"W0_p95_us={w0p95:F1} W_p95_us={wp95:F1} delta_p95_us={wp95 - w0p95:F1} "
                         + $"W0_p99_us={w0p99:F1} W_p99_us={wp99:F1} delta_p99_us={wp99 - w0p99:F1} "
                         + $"W0_max_us={w0max:F1} W_max_us={wmax:F1} delta_max_us={wmax - w0max:F1} "
                         + $"baseline_passes={baseline.Count} flood_passes={outcome.ProcessMicros.Count} "
                         + $"victim_throughput_ratio={victimThroughputRatio:F3}", "unknown"));

                    if (sustained) break;
                }

                for (int i = firstRowOfRate; i < rowLines.Count; i++)
                {
                    rowLines[i] = (rowLines[i].Line, firstAttemptSustained ? "yes" : "no");
                }

                if (firstAttemptSustained)
                {
                    lastSustainedGas = targetGas;
                }
                else
                {
                    sustainedEveryPoint = false;
                    firstFailedGas = targetGas;
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            failure = ex;
        }

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
            if (failure is null) failure = ex;
            else TestContext.Out.WriteLine($"DEBUG the after-baseline re-measurement also failed: {ex.GetType().Name}: {ex.Message}");
        }

        foreach ((string line, string accepted) in rowLines) Emit($"{line} accepted={accepted} {driftFields}");

        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();

        Emit($"case={summaryCase} shape={shape} ceiling={ceiling} shedding={(_shedding ? "on" : "off")} "
             + $"{extraFields}cpus={ObservedCpuSet()} single_core={(IsSingleCore() ? "yes" : "no")} "
             + $"declared_gas_per_tx={declaredGasPerTx} capacity_declared_gas_per_s={lastSustainedGas} "
             + $"capacity_upper={(sustainedEveryPoint ? "unbounded" : firstFailedGas.ToString())} "
             + $"censored={(sustainedEveryPoint ? "yes" : "no")} capacity_basis=first_attempt note=B_not_fixed {driftFields}");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(baselineDriftPct, Is.LessThan(BrokenBaselineDriftPercent),
                $"the two idle baselines' medians disagree by {baselineDriftPct:F1}%, so the ramp spans different machine states");
            Assert.That(baselineTailDriftPct, Is.LessThan(BrokenBaselineTailDriftPercent),
                $"the two idle baselines' p99s disagree by {baselineTailDriftPct:F1}%, so the ramp spans different machine states");
        }
    }

    /// <summary>Maps the declared gas/s grid onto integer rates for one flood transaction's declared gas.</summary>
    private static List<(int Rate, long TargetGasPerSecond)> GasGrid(ulong declaredGasPerTx)
    {
        List<(int, long)> grid = [];
        int lastRate = 0;
        foreach (int millions in DeclaredGasRateGridMillions)
        {
            long target = millions * 1_000_000L;
            int rate = Math.Max(1, (int)Math.Round((double)target / declaredGasPerTx, MidpointRounding.AwayFromZero));
            if (rate == lastRate) continue;
            grid.Add((rate, target));
            lastRate = rate;
        }
        return grid;
    }

    /// <summary>Distinct transactions for every submission in a window: a reused hash would be refused from the
    /// known cache without running the work the row claims.</summary>
    private static int PoolSizeFor(int rate, TimeSpan floodSpan) =>
        Math.Max(FloodPoolSize, (int)Math.Ceiling(rate * (floodSpan.TotalSeconds + 2) * 1.25));

    /// <summary>Processes the compute victim once per period, never catching up on an overrun, and returns each
    /// block's duration.</summary>
    private List<double> MeasurePeriodicComputeBlocks(TimeSpan window)
    {
        List<double> micros = [];
        long period = (long)(ComputeVictimPeriod.TotalSeconds * Stopwatch.Frequency);
        long start = Stopwatch.GetTimestamp();
        long end = start + (long)(window.TotalSeconds * Stopwatch.Frequency);
        for (long due = start; due < end; due += period)
        {
            WaitUntil(due, CancellationToken.None);
            long blockStart = Stopwatch.GetTimestamp();
            Volatile.Write(ref _victimActive, true);
            try
            {
                _chain.BranchProcessor.Process(_parent, [_computeBlock], ProcessingOptions.NoValidation, NullBlockTracer.Instance);
            }
            finally
            {
                Volatile.Write(ref _victimActive, false);
            }
            micros.Add(Stopwatch.GetElapsedTime(blockStart).TotalMicroseconds);

            long now = Stopwatch.GetTimestamp();
            while (due + period < now)
            {
                due += period;
                _missedVictimPeriods++;
            }
        }
        return micros;
    }

    private void AssertComputeBlockDoesRealWork()
    {
        BlockReceiptsTracer receiptsTracer = new();
        receiptsTracer.SetOtherTracer(NullBlockTracer.Instance);
        receiptsTracer.StartNewBlockTrace(_computeBlock);
        _chain.BranchProcessor.Process(_parent, [_computeBlock], ProcessingOptions.NoValidation, receiptsTracer);
        receiptsTracer.EndBlockTrace();

        long gasUsed = 0;
        foreach (TxReceipt receipt in receiptsTracer.TxReceipts) gasUsed += (long)receipt.GasUsed;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(receiptsTracer.TxReceipts.Length, Is.EqualTo(ComputeVictimCalls),
                "the compute victim block did not execute its calls");
            Assert.That(gasUsed, Is.EqualTo(ComputeVictimCalls * ComputeVictimCallGas),
                "each compute call must burn its whole gas limit in the loop, or the victim is lighter than labelled");
        }

        Emit($"case=compute_block calls={ComputeVictimCalls} receipts={receiptsTracer.TxReceipts.Length} gas_used={gasUsed}");
    }

    private Block BuildComputeBlock()
    {
        Transaction[] calls = new Transaction[ComputeVictimCalls];
        for (int i = 0; i < calls.Length; i++)
        {
            calls[i] = Build.A.Transaction
                .WithNonce((ulong)i)
                .WithTo(ComputeVictimContract)
                .WithGasLimit(ComputeVictimCallGas)
                .WithGasPrice(1.GWei)
                .SignedAndResolved(BlockSenderKey)
                .TestObject;
        }

        return Build.A.Block
            .WithNumber(_parent.Number + 1)
            .WithParent(_parent)
            .WithGasLimit(BlockGasLimit)
            .WithBaseFeePerGas(UInt256.Zero)
            .WithTransactions(calls)
            .TestObject;
    }

    /// <summary>Adds one empty block, so the pool and the simulator's per-head budget see a new head.</summary>
    private void AdvanceHead()
    {
        Hash256? before = _chain.BlockTree.Head?.Hash;
        _chain.AddBlock().GetAwaiter().GetResult();
        Assert.That(_chain.BlockTree.Head?.Hash, Is.Not.EqualTo(before), "the head did not advance");
    }

    /// <summary>Submits flood transactions straight to the pool, or as one-transaction gossip tasks on the node's
    /// <see cref="BackgroundTaskScheduler"/> bound to the chain's main branch processor.</summary>
    /// <remarks>A task whose token is already cancelled when it runs has expired while block processing held the
    /// queue; it is dropped, as <c>HandleSlow</c> drops a message cancelled before its first transaction. A task
    /// already inside <c>SubmitTx</c> when a block starts runs to completion: the pool takes no token.</remarks>
    private sealed class GossipSubmitter : IAsyncDisposable
    {
        private readonly ITxPool _pool;
        private readonly BackgroundTaskScheduler? _scheduler;
        private readonly Func<bool> _victimActive;
        private readonly Func<FloodTxRequest, CancellationToken, Task> _handle;
        private int _scheduled;
        private int _refused;
        private int _executed;
        private int _dropped;
        private int _starts;
        private int _startsDuringVictim;

        public GossipSubmitter(FloodTestBlockchain chain, bool scheduled, Func<bool> victimActive)
        {
            _pool = chain.TxPool;
            _victimActive = victimActive;
            _handle = Handle;
            if (scheduled)
            {
                _scheduler = new BackgroundTaskScheduler(chain.BranchProcessor, chain.ChainHeadInfoProvider,
                    GossipSchedulerConcurrency, GossipSchedulerCapacity, LimboLogs.Instance);
            }
        }

        public readonly record struct Counts(int Scheduled, int Refused, int Executed, int Dropped, int Starts, int StartsDuringVictim)
        {
            public static Counts operator -(Counts a, Counts b) => new(a.Scheduled - b.Scheduled, a.Refused - b.Refused,
                a.Executed - b.Executed, a.Dropped - b.Dropped, a.Starts - b.Starts, a.StartsDuringVictim - b.StartsDuringVictim);
        }

        public bool IsScheduled => _scheduler is not null;

        public Counts Read() => new(Volatile.Read(ref _scheduled), Volatile.Read(ref _refused), Volatile.Read(ref _executed),
            Volatile.Read(ref _dropped), Volatile.Read(ref _starts), Volatile.Read(ref _startsDuringVictim));

        /// <remarks>The scheduled path returns before the pool decides, so its result says only that the task was
        /// queued; the caller counts rejections from the pool's metrics.</remarks>
        public AcceptTxResult Submit(Transaction tx)
        {
            if (_scheduler is null) return Run(tx);

            if (_scheduler.TryScheduleTask(new FloodTxRequest(tx), _handle, GossipTaskTimeout)) Interlocked.Increment(ref _scheduled);
            else Interlocked.Increment(ref _refused);
            return AcceptTxResult.Accepted;
        }

        public bool WaitIdle(TimeSpan timeout)
        {
            if (_scheduler is null) return true;
            long end = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
            while (Stopwatch.GetTimestamp() < end)
            {
                if (Volatile.Read(ref _executed) + Volatile.Read(ref _dropped) >= Volatile.Read(ref _scheduled)) return true;
                Thread.Sleep(5);
            }
            return false;
        }

        private Task Handle(FloodTxRequest request, CancellationToken token)
        {
            if (token.IsCancellationRequested) Interlocked.Increment(ref _dropped);
            else Run(request.Tx);
            return Task.CompletedTask;
        }

        private AcceptTxResult Run(Transaction tx)
        {
            Interlocked.Increment(ref _starts);
            if (_victimActive()) Interlocked.Increment(ref _startsDuringVictim);
            AcceptTxResult result = _pool.SubmitTx(tx, TxHandlingOptions.None);
            Interlocked.Increment(ref _executed);
            return result;
        }

        public async ValueTask DisposeAsync()
        {
            if (_scheduler is not null) await _scheduler.DisposeAsync();
        }
    }

    private readonly record struct FloodTxRequest(Transaction Tx) : IBackgroundTaskRequest<FloodTxRequest>;
}
