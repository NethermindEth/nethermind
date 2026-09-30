// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.TxPool;
using NUnit.Framework;

using static Nethermind.Blockchain.Test.MeasurementEnvironment;
using static Nethermind.Blockchain.Test.MeasurementStatistics;

namespace Nethermind.Blockchain.Test;

/// <summary>
/// The campaign's three flood suites, each measured at fixed declared VERIFY gas/s levels so any delay budget can be
/// applied after the run: block building under spam (<c>producer</c>), block import on a node with its gossip
/// protections (<c>import</c>), and honest frame transactions lost once spam spends the per-head budget (<c>honest</c>).
/// </summary>
public partial class FrameTxFloodMeasurement
{
    public const string CostSuite = "frame-tx-cost";
    public const string ProducerSuite = "frame-tx-producer";
    public const string ImportSuite = "frame-tx-import";
    public const string ImportTailsSuite = "frame-tx-import-tails";
    public const string HonestSuite = "frame-tx-honest";

    /// <summary>100,000 is today's EIP value: the reference the other ceilings are compared against.</summary>
    private static readonly ulong[] ProducerCeilings = [100_000, 235_800, 250_000, 300_000, 400_000, 500_000];
    private static readonly ulong[] ImportCeilings = [235_800, 250_000, 300_000, 400_000];
    private static readonly ulong[] HonestCeilings = [235_800, 300_000];
    private static readonly string[] BoundShapes = ["keccak-wide", "signature-stuffed"];

    /// <summary>5M steps, so a delay budget reads off at the resolution the ramps had; 5M keeps a slower runner off the floor.</summary>
    private static readonly int[] ProducerLevelsMillions = [5, 10, 15, 20, 25, 30, 35, 40];
    private static readonly int[] ImportLevelsMillions = [5, 10, 30, 50];

    /// <summary>0 is the control: honest traffic alone must be admitted.</summary>
    private static readonly int[] HonestLevelsMillions = [0, 5, 10, 30, 50];

    private static readonly Address ComputeVictimContract = TestItem.AddressD;

    /// <summary>Calls in the compute victim block, each burning its whole gas limit in the keccak-wide loop.</summary>
    /// <remarks>10M gas of 4 KiB hashing takes tens of milliseconds, the order of a mainnet block's processing
    /// time. It is a time proxy: neither the gas nor the content of a mainnet block.</remarks>
    private const int ComputeVictimCalls = 10;

    private const long ComputeVictimCallGas = 1_000_000;

    /// <summary>Victim cadence: leaves most of each period idle, as a node is between blocks.</summary>
    private static readonly TimeSpan ComputeVictimPeriod = TimeSpan.FromMilliseconds(250);

    private static readonly TimeSpan PeriodicWindow = TimeSpan.FromSeconds(10);

    /// <summary>Long windows for the tail suite: ~240 compute blocks per level, enough for a p99 that is not the max.</summary>
    private static readonly TimeSpan TailWindow = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan PeriodicWarmup = TimeSpan.FromSeconds(1);

    /// <summary>Window of the throw-away first flood of a cell, which pays the generator's cold start.</summary>
    private static readonly TimeSpan ColdStartWindow = TimeSpan.FromSeconds(3);

    /// <summary>A victim sample this many times the idle median counts as an outlier: the stalls the tail metrics
    /// must be read against.</summary>
    private const double OutlierFactor = 5;

    /// <summary>Back-to-back compute blocks before the first periodic baseline of the process.</summary>
    /// <remarks>The fixture warm-up runs transfer blocks only. Without this, the compute path finishes tiering
    /// during the measurement: on one pinned core the background JIT stalls the victim.</remarks>
    private static readonly TimeSpan ComputeFixtureWarmup = TimeSpan.FromSeconds(30);

    private static bool _computeWarmed;

    /// <summary>The node's gossip scheduler settings: <c>InitConfig</c> defaults and the scheduler's own deadline.</summary>
    private const int GossipSchedulerConcurrency = 2;

    private const int GossipSchedulerCapacity = 2_048;

    private static readonly TimeSpan GossipTaskTimeout = TimeSpan.FromSeconds(2);

    /// <summary>In-flight handlers can finish on either side of a window edge, on each worker.</summary>
    private const int ScheduledAccountingSlack = 2 * GossipSchedulerConcurrency + 2;

    /// <summary>Head cadence for the honest suite: one mainnet slot.</summary>
    private static readonly TimeSpan SlotLength = TimeSpan.FromSeconds(12);

    private const int HonestSlotsPerLevel = 3;

    private const int HonestRate = 5;

    private const ulong HonestVerifyGas = 50_000;

    /// <summary>Distinct honest senders, one transaction each, enough for every level of one ceiling.</summary>
    private static int HonestSenderCount => HonestLevelsMillions.Sum(static m => HonestTxCount(HonestSlotsFor(m)));

    private const double HonestControlAdmittedFloor = 0.95;

    private Block _computeBlock = null!;

    private bool _victimActive;

    private int _missedVictimPeriods;

    private static IEnumerable<TestCaseData> ProducerCases() =>
        from ceiling in ProducerCeilings from shape in BoundShapes select new TestCaseData(ceiling, shape);

    private static IEnumerable<TestCaseData> ImportCases() =>
        from ceiling in ImportCeilings
        from shape in BoundShapes
        from path in new[] { "direct", "protected" }
        select new TestCaseData(ceiling, shape, path);

    private static IEnumerable<TestCaseData> HonestCases() => HonestCeilings.Select(static c => new TestCaseData(c));

    /// <summary>Block building while the flood runs: the producer re-executes one failing transaction sized to the
    /// ceiling, as a builder does for a frame transaction that fails at build time.</summary>
    [TestCaseSource(nameof(ProducerCases))]
    [Category(ProducerSuite)]
    public async Task Producer_delay_at_fixed_declared_gas_rate(ulong ceiling, string shape)
    {
        SkipUnlessSingleCore();
        if (shape != "signature-stuffed") Eip8141MeasurementGuards.SkipIfCeilingUnreachable(ceiling);
        await BuildChain(shape, ceiling);

        using ProducerRig rig = ProducerRig.Create(_chain, kRetry: 1, [FrameTx(0, ceiling, shape)], BlockGasLimit);
        rig.RunFor(WarmupWindow);
        Func<long>? rejectionCounter = RejectionCounterFor(shape);
        await using GossipSubmitter submitter = new(_chain, scheduled: false, static () => false);

        RunLevels(ceiling, shape, "producer_level", "victim=producer_pass gossip_path=direct ", ProducerLevelsMillions,
            () => rig.Measure(MeasureWindow),
            rate => DirectPoint(MeasureAccounted(submitter, RefusalCounterFor(shape), () => MeasureUnderFloodGeneric(rate,
                warmup: () => { Thread.Sleep(FloodSettle); rig.RunFor(WarmupWindow); },
                measure: rig.Measure, rejectionCounter, onWindowStart: rig.MarkWindowStart, submit: submitter.Submit,
                poolSize: PoolSizeFor(rate, FloodSettle + WarmupWindow + MeasureWindow)))));

        Assert.That(rig.FailingExecutions, Is.GreaterThan(0),
            "the producer never re-executed the failing transaction, so this measures an ordinary block");
    }

    /// <summary>Block import on a ~10M gas compute block processed every 250 ms. <c>direct</c>: the flood goes
    /// straight to the pool and no processing flag is raised, a counterfactual node without any gossip protection (not
    /// the node before #13994, whose scheduler already paused for block processing); <c>protected</c>: the flag is
    /// raised while the block runs and the flood goes through the gossip scheduler, as a node receives gossip.</summary>
    [TestCaseSource(nameof(ImportCases))]
    [Category(ImportSuite)]
    public async Task Import_delay_at_fixed_declared_gas_rate(ulong ceiling, string shape, string path) =>
        await MeasureImport(ceiling, shape, path, "import_level", PeriodicWindow);

    /// <summary>The import arm with 60 s windows, so the p99 rests on ~240 blocks instead of ~40.</summary>
    /// <remarks>About 100 minutes, over the 80-minute budget: split and time it before dispatching it, alone, in a
    /// window agreed with the runner's owners.</remarks>
    [TestCaseSource(nameof(ImportCases))]
    [Category(ImportTailsSuite)]
    public async Task Import_tail_delay_at_fixed_declared_gas_rate(ulong ceiling, string shape, string path) =>
        await MeasureImport(ceiling, shape, path, "import_tails_level", TailWindow);

    private async Task MeasureImport(ulong ceiling, string shape, string path, string rowCase, TimeSpan window)
    {
        SkipUnlessSingleCore();
        if (shape != "signature-stuffed") Eip8141MeasurementGuards.SkipIfCeilingUnreachable(ceiling);
        await BuildChain(shape, ceiling, computeVictim: true);
        AssertComputeBlockDoesRealWork();
        if (!_computeWarmed)
        {
            long warmEnd = Stopwatch.GetTimestamp() + (long)(ComputeFixtureWarmup.TotalSeconds * Stopwatch.Frequency);
            while (Stopwatch.GetTimestamp() < warmEnd) ProcessImport(_computeBlock);
            _computeWarmed = true;
        }

        bool isProtected = path == "protected";
        s_blockProcessingSignal = isProtected;
        await using GossipSubmitter submitter = new(_chain, scheduled: isProtected, () => Volatile.Read(ref _victimActive));

        // The scheduled path returns before the pool sees the transaction, so rejections come from the pool's own
        // counters on both paths.
        Func<long> rejectionCounter = shape == "signature-stuffed"
            ? () => Nethermind.TxPool.Metrics.PendingTransactionsFrameTxSignatureInvalid
            : () => Volatile.Read(ref Nethermind.TxPool.Metrics.PendingTransactionsFrameTxSimulationFailed);

        LevelPoint Level(int rate, TimeSpan levelWindow)
        {
            GossipSubmitter.Counts atStart = default, atEnd = default;
            GossipSubmitter.Counts levelStart = submitter.Read();
            int missedAtStart = 0;
            FloodOutcome outcome = MeasureAccounted(submitter, RefusalCounterFor(shape), () => MeasureUnderFloodGeneric(rate,
                warmup: () => { Thread.Sleep(FloodSettle); MeasurePeriodicComputeBlocks(PeriodicWarmup); },
                measure: MeasurePeriodicComputeBlocks, rejectionCounter,
                onWindowStart: () => { atStart = submitter.Read(); missedAtStart = _missedVictimPeriods; },
                submit: submitter.Submit, window: levelWindow,
                poolSize: PoolSizeFor(rate, FloodSettle + PeriodicWarmup + levelWindow),
                onWindowEnd: () => atEnd = submitter.Read()));

            // Read once the scheduler has drained: at the window's end a queued task is neither run nor expired yet.
            GossipSubmitter.Counts level = submitter.Read() - levelStart;
            GossipSubmitter.Counts inWindow = atEnd - atStart;
            double executedRatio = outcome.Submitted > 0 ? (double)inWindow.Executed / outcome.Submitted : 0;
            double duringVictimPct = inWindow.Starts > 0 ? 100.0 * inWindow.StartsDuringVictim / inWindow.Starts : 0;
            // The scheduler kept up if it neither refused a task for a full queue nor expired one past its deadline:
            // every check then started within GossipTaskTimeout of its arrival.
            bool keptUp = !isProtected || (level.Dropped == 0 && level.Refused == 0);
            return new LevelPoint(outcome, keptUp,
                $"scheduled={inWindow.Scheduled} executed={inWindow.Executed} level_dropped={level.Dropped} "
                + $"level_refused={level.Refused} window_executed_ratio={executedRatio:F3} handler_starts={inWindow.Starts} "
                + $"handler_starts_during_victim_pct={duringVictimPct:F1} missed_victim_periods={_missedVictimPeriods - missedAtStart} ");
        }

        RunLevels(ceiling, shape, rowCase,
            $"victim=compute_block gossip_path={path} victim_gas={ComputeVictimCalls * ComputeVictimCallGas} "
            + $"victim_period_ms={ComputeVictimPeriod.TotalMilliseconds:F0} window_s={window.TotalSeconds:F0} ",
            ImportLevelsMillions, () => MeasurePeriodicComputeBlocks(window), rate => Level(rate, window),
            tailDriftFailsTest: false, coldStart: rate => Level(rate, ColdStartWindow));
    }

    /// <summary>Honest frame transactions offered at a steady rate while keccak-wide spam runs, with a new head every
    /// slot and the node's stock per-head simulation budget. Level 0 is the control.</summary>
    [TestCaseSource(nameof(HonestCases))]
    [Category(HonestSuite)]
    public async Task Honest_frame_tx_admission_under_spam(ulong ceiling)
    {
        SkipUnlessSingleCore();
        Eip8141MeasurementGuards.SkipIfCeilingUnreachable(ceiling);
        await BuildChain("keccak-wide", ceiling, shedding: true, honestSenders: HonestSenderCount);
        ulong declaredGasPerTx = FrameTxValidation.ValidationWorkGas(FloodFrameTx(0));

        int cursor = 0;
        foreach (int millions in HonestLevelsMillions)
        {
            int slots = HonestSlotsFor(millions);
            int spamRate = millions == 0 ? 0 : RateFor(millions, declaredGasPerTx);
            HonestOutcome o = RunHonestSlots(spamRate, slots, ref cursor);

            Emit($"case=honest_level shape=keccak-wide ceiling={ceiling} shedding=on {SimBudgetField} {CpuFields} "
                 + $"target_declared_gas_per_s={millions * 1_000_000L} declared_gas_per_tx={declaredGasPerTx} spam_rate={spamRate} "
                 + $"slots={slots} slot_s={SlotLength.TotalSeconds:F0} honest_rate={HonestRate} honest_verify_gas={HonestVerifyGas} "
                 + $"honest_offered={o.Honest.Total} honest_admitted={o.Honest.Accepted} honest_deferred={o.Honest.Deferred} "
                 + $"honest_deferred_budget={o.Honest.DeferredBudget} honest_deferred_preempted={o.Honest.DeferredPreempted} "
                 + $"honest_deferred_busy={o.Honest.DeferredBusy} honest_other={o.Honest.Other} "
                 + $"honest_admitted_pct={o.Honest.AcceptedPct:F1} honest_lost_to_budget_pct={o.Honest.BudgetPct:F1} "
                 + $"honest_admitted_pct_0_1s={o.PhasePct(0):F1} honest_admitted_pct_1_3s={o.PhasePct(1):F1} "
                 + $"honest_admitted_pct_3_6s={o.PhasePct(2):F1} honest_admitted_pct_6_12s={o.PhasePct(3):F1} "
                 + $"first_honest_budget_defer_after_head_ms={(o.FirstDeferMs.Count > 0 ? Median(o.FirstDeferMs).ToString("F0") : "none")} "
                 + $"spam_submitted={o.Spam.Total} spam_rejected={o.Spam.Failed} spam_deferred={o.Spam.Deferred} spam_other={o.Spam.Other}");

            if (millions == 0)
            {
                // Without spam nothing may be lost to the budget. Deferrals while the head block imports, or while
                // the head-change sweep holds the simulator, are transient: a node refetches those from a peer.
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(o.Honest.DeferredBudget, Is.Zero, "honest traffic alone spent the per-head budget");
                    Assert.That((o.Honest.Accepted + o.Honest.DeferredPreempted + o.Honest.DeferredBusy) / (double)o.Honest.Total,
                        Is.GreaterThanOrEqualTo(HonestControlAdmittedFloor),
                        "honest frame transactions are not admitted even without spam, so they are not valid honest traffic");
                }
            }
        }
    }

    private readonly record struct LevelPoint(FloodOutcome Outcome, bool ArmSustained, string Fields);

    private static LevelPoint DirectPoint(FloodOutcome outcome) => new(outcome, true, "");

    private static int HonestSlotsFor(int millions) => millions == 0 ? 1 : HonestSlotsPerLevel;

    /// <summary>Honest transactions a level offers: its slots plus 2 s of slack, with a quarter to spare.</summary>
    private static int HonestTxCount(int slots) => (int)Math.Ceiling(HonestRate * (SlotLength.TotalSeconds * slots + 2) * 1.25);

    private static int RateFor(int millions, ulong declaredGasPerTx) =>
        Math.Max(1, (int)Math.Round(millions * 1_000_000d / declaredGasPerTx, MidpointRounding.AwayFromZero));

    /// <summary>Measures every fixed declared gas/s level once, bracketed by idle baselines, and emits one row per
    /// level. No level stops the sequence: a delay budget is applied at analysis, not here.</summary>
    private void RunLevels(
        ulong ceiling, string shape, string rowCase, string extraFields, int[] levelsMillions,
        Func<List<double>> measureBaseline, Func<int, LevelPoint> measureAtRate, bool tailDriftFailsTest = true,
        Func<int, LevelPoint>? coldStart = null)
    {
        ulong declaredGasPerTx = FrameTxValidation.ValidationWorkGas(FloodFrameTx(0));

        (List<double> Samples, GcDelta Gc) Baseline()
        {
            GcDelta.Snapshot start = GcDelta.Take();
            List<double> samples = measureBaseline();
            return (samples, GcDelta.Since(start));
        }

        // The first flood pays the generator's cold start; it is not a level.
        (coldStart ?? measureAtRate)(RateFor(levelsMillions[0], declaredGasPerTx));

        (List<double> baseline, GcDelta baselineGc) = Baseline();
        double w0 = Percentile(baseline, 0.50);
        double w0p95 = Percentile(baseline, 0.95);
        double w0p99 = Percentile(baseline, 0.99);
        double w0max = Percentile(baseline, 1.0);
        double outlierUs = w0 * OutlierFactor;

        List<string> rows = [];
        Exception? failure = null;
        try
        {
            foreach (int millions in levelsMillions)
            {
                int rate = RateFor(millions, declaredGasPerTx);
                LevelPoint point = measureAtRate(rate);
                FloodOutcome outcome = point.Outcome;
                double periodUs = 1_000_000.0 / rate;
                bool rateHeld = outcome.AchievedRate >= rate * RateHeldFloor;
                bool lagBounded = outcome.MaxLagUs <= periodUs * MaxSustainedLagPeriods;
                bool sustained = rateHeld && lagBounded && point.ArmSustained;

                double w = Percentile(outcome.ProcessMicros, 0.50);
                double wp95 = Percentile(outcome.ProcessMicros, 0.95);
                double wp99 = Percentile(outcome.ProcessMicros, 0.99);
                double wmax = Percentile(outcome.ProcessMicros, 1.0);
                double victimThroughputRatio = baseline.Count > 0 ? (double)outcome.ProcessMicros.Count / baseline.Count : 0;

                rows.Add($"case={rowCase} shape={shape} ceiling={ceiling} shedding={(_shedding ? "on" : "off")} {SimBudgetField} "
                         + $"{extraFields}{CpuFields} "
                         + $"target_declared_gas_per_s={millions * 1_000_000L} declared_gas_per_tx={declaredGasPerTx} "
                         + $"offered_rate={rate} achieved_rate={outcome.AchievedRate:F1} sustained={(sustained ? "yes" : "no")} "
                         + $"rate_held={(rateHeld ? "yes" : "no")} lag_bounded={(lagBounded ? "yes" : "no")} "
                         + $"arm_sustained={(point.ArmSustained ? "yes" : "no")} "
                         + $"max_lag_us={outcome.MaxLagUs:F0} lag_budget_us={periodUs * MaxSustainedLagPeriods:F0} "
                         + $"submitted={outcome.Submitted} rejected={outcome.Rejected} shed={outcome.Shed} shed_pct={ShedPct(outcome):F1} "
                         + point.Fields
                         + $"W0_p50_us={w0:F1} W_p50_us={w:F1} delta_p50_us={w - w0:F1} "
                         + $"W0_p95_us={w0p95:F1} W_p95_us={wp95:F1} delta_p95_us={wp95 - w0p95:F1} "
                         + $"W0_p99_us={w0p99:F1} W_p99_us={wp99:F1} delta_p99_us={wp99 - w0p99:F1} "
                         + $"W0_max_us={w0max:F1} W_max_us={wmax:F1} delta_max_us={wmax - w0max:F1} "
                         + $"baseline_passes={baseline.Count} flood_passes={outcome.ProcessMicros.Count} "
                         + $"victim_throughput_ratio={victimThroughputRatio:F3} "
                         + $"outlier_threshold_us={outlierUs:F0} W0_outliers={Outliers(baseline, outlierUs)} "
                         + $"W_outliers={Outliers(outcome.ProcessMicros, outlierUs)} {baselineGc.Fields("W0_")} {outcome.Gc.Fields("W_")} "
                         + $"host_load1={HostLoad1()}");
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
            (List<double> baselineAfter, GcDelta afterGc) = Baseline();
            baselineDriftPct = w0 <= 0 ? 0 : Math.Abs(Percentile(baselineAfter, 0.50) - w0) / w0 * 100;
            baselineTailDriftPct = w0p99 <= 0 ? 0 : Math.Abs(Percentile(baselineAfter, 0.99) - w0p99) / w0p99 * 100;
            // Median and tail are judged apart: a noisy p99 must not invalidate a median that held.
            bool medianValid = baselineDriftPct < MaxBaselineDriftPercent;
            bool tailValid = baselineTailDriftPct < MaxBaselineTailDriftPercent;
            driftFields = $"baseline_drift_pct={baselineDriftPct:F1} baseline_tail_drift_pct={baselineTailDriftPct:F1} "
                          + $"W0b_outliers={Outliers(baselineAfter, outlierUs)} {afterGc.Fields("W0b_")} "
                          + $"valid={(medianValid ? "yes" : "no")} tail_valid={(tailValid ? "yes" : "no")}";
        }
        catch (Exception ex)
        {
            driftFields = "baseline_drift_pct=NaN baseline_tail_drift_pct=NaN W0b_outliers=NaN valid=no tail_valid=no";
            if (failure is null) failure = ex;
            else TestContext.Out.WriteLine($"DEBUG the after-baseline re-measurement also failed: {ex.GetType().Name}: {ex.Message}");
        }

        foreach (string row in rows) Emit($"{row} {driftFields}");

        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(baselineDriftPct, Is.LessThan(BrokenBaselineDriftPercent),
                $"the two idle baselines' medians disagree by {baselineDriftPct:F1}%, so the levels span different machine states");
            // The import victims give ~40 blocks per 10 s window, so their p99 is close to the max and one host hiccup
            // moves it: there a broken tail only voids the tail metrics (tail_valid=no) and the median stands.
            if (tailDriftFailsTest)
            {
                Assert.That(baselineTailDriftPct, Is.LessThan(BrokenBaselineTailDriftPercent),
                    $"the two idle baselines' p99s disagree by {baselineTailDriftPct:F1}%, so the levels span different machine states");
            }
        }
    }

    /// <summary>Pool refusals a flood caused: the shape's rejection counter plus admission sheds.</summary>
    private static Func<long> RefusalCounterFor(string shape) => shape == "signature-stuffed"
        ? () => Nethermind.TxPool.Metrics.PendingTransactionsFrameTxSignatureInvalid + ShedCount()
        : () => Volatile.Read(ref Nethermind.TxPool.Metrics.PendingTransactionsFrameTxSimulationFailed) + ShedCount();

    /// <summary>Runs one level and checks, over the whole level, that the pool refused or shed every submission.</summary>
    /// <remarks>A window's counters are read while the generator still submits, so at hundreds of tx/s they can
    /// disagree by a few transactions without any going missing. Once the generator has joined and the scheduler has
    /// drained, nothing writes them.</remarks>
    private static FloodOutcome MeasureAccounted(GossipSubmitter submitter, Func<long> refusals, Func<FloodOutcome> measure)
    {
        long refusedBefore = refusals();
        int executedBefore = submitter.Read().Executed;
        FloodOutcome outcome = measure();
        Assert.That(submitter.WaitIdle(TimeSpan.FromSeconds(10)), Is.True,
            "the gossip scheduler did not drain after the window, so the next level would inherit its backlog");

        long refused = refusals() - refusedBefore;
        int executed = submitter.Read().Executed - executedBefore;
        Assert.That(Math.Abs(refused - executed), Is.LessThanOrEqualTo(submitter.IsScheduled ? ScheduledAccountingSlack : 1),
            $"the pool refused or shed {refused} of {executed} submissions in this level; the rest went missing");
        return outcome;
    }

    /// <summary>Distinct transactions for every submission in a window: a reused hash would be refused from the
    /// known cache without running the work the row claims.</summary>
    private static int PoolSizeFor(int rate, TimeSpan floodSpan) =>
        Math.Max(FloodPoolSize, (int)Math.Ceiling(rate * (floodSpan.TotalSeconds + 2) * 1.25));

    /// <summary>Runs honest traffic, and spam when <paramref name="spamRate"/> is positive, over whole slots, each
    /// opened by a new head.</summary>
    private HonestOutcome RunHonestSlots(int spamRate, int slots, ref int cursor)
    {
        TimeSpan span = SlotLength * slots;
        int honestCount = HonestTxCount(slots);
        Assert.That(cursor + honestCount, Is.LessThanOrEqualTo(HonestSenderCount),
            "not enough funded honest senders for one transaction each");
        Transaction[] honest = new Transaction[honestCount];
        for (int i = 0; i < honestCount; i++) honest[i] = HonestFrameTx(HonestSender(cursor + i));
        cursor += honestCount;

        HonestOutcome outcome = new();
        using FloodGenerator honestGenerator = new(tx => outcome.RecordHonest(_chain.TxPool.SubmitTx(tx, TxHandlingOptions.None)), honest, HonestRate);

        void Slots()
        {
            for (int s = 0; s < slots; s++)
            {
                AdvanceHead();
                outcome.MarkHead();
                Thread.Sleep(SlotLength);
                outcome.CloseSlot();
            }
        }

        if (spamRate == 0)
        {
            honestGenerator.Run(() => { Slots(); return 0; });
            return outcome;
        }

        int spamCount = PoolSizeFor(spamRate, span);
        Transaction[] spam = BuildFloodTransactions(_saltCursor, spamCount);
        _saltCursor += spamCount;
        using FloodGenerator spamGenerator = new(tx => outcome.RecordSpam(_chain.TxPool.SubmitTx(tx, TxHandlingOptions.None)), spam, spamRate);
        honestGenerator.Run(() => spamGenerator.Run(() => { Slots(); return 0; }));
        return outcome;
    }

    /// <summary>Counts honest and spam outcomes, and when honest admissions start being deferred after each head.</summary>
    private sealed class HonestOutcome
    {
        private static readonly double[] PhaseEndsS = [1, 3, 6, double.PositiveInfinity];
        private readonly object _lock = new();
        private long _headTimestamp = Stopwatch.GetTimestamp();
        private double? _firstDeferInSlotMs;
        private readonly int[] _phaseOffered = new int[PhaseEndsS.Length];
        private readonly int[] _phaseAccepted = new int[PhaseEndsS.Length];

        public Tally Honest { get; } = new();
        public Tally Spam { get; } = new();
        public List<double> FirstDeferMs { get; } = [];

        public void MarkHead()
        {
            lock (_lock)
            {
                _headTimestamp = Stopwatch.GetTimestamp();
                _firstDeferInSlotMs = null;
            }
        }

        public void CloseSlot()
        {
            lock (_lock)
            {
                if (_firstDeferInSlotMs is double ms) FirstDeferMs.Add(ms);
            }
        }

        public AcceptTxResult RecordHonest(AcceptTxResult result)
        {
            Honest.Add(result);
            lock (_lock)
            {
                double sinceHeadS = Stopwatch.GetElapsedTime(_headTimestamp).TotalSeconds;
                int phase = Array.FindIndex(PhaseEndsS, end => sinceHeadS < end);
                _phaseOffered[phase]++;
                if (result == AcceptTxResult.Accepted) _phaseAccepted[phase]++;
                else if (result == AcceptTxResult.FrameSimulationDeferred && result.ToString().Contains("budget", StringComparison.Ordinal))
                {
                    _firstDeferInSlotMs ??= sinceHeadS * 1000;
                }
            }
            return result;
        }

        public AcceptTxResult RecordSpam(AcceptTxResult result)
        {
            Spam.Add(result);
            return result;
        }

        public double PhasePct(int phase)
        {
            lock (_lock)
            {
                return _phaseOffered[phase] > 0 ? 100.0 * _phaseAccepted[phase] / _phaseOffered[phase] : double.NaN;
            }
        }
    }

    /// <summary>Counts outcomes; a deferral is split by the simulator's reason, since only a spent budget loses the
    /// transaction for the rest of the slot.</summary>
    private sealed class Tally
    {
        private int _accepted, _deferredBudget, _deferredPreempted, _deferredBusy, _failed, _other;

        public int Accepted => Volatile.Read(ref _accepted);
        public int DeferredBudget => Volatile.Read(ref _deferredBudget);
        public int DeferredPreempted => Volatile.Read(ref _deferredPreempted);
        public int DeferredBusy => Volatile.Read(ref _deferredBusy);
        public int Deferred => DeferredBudget + DeferredPreempted + DeferredBusy;
        public int Failed => Volatile.Read(ref _failed);
        public int Other => Volatile.Read(ref _other);
        public int Total => Accepted + Deferred + Failed + Other;
        public double AcceptedPct => Total > 0 ? 100.0 * Accepted / Total : double.NaN;
        public double BudgetPct => Total > 0 ? 100.0 * DeferredBudget / Total : double.NaN;

        public void Add(AcceptTxResult result)
        {
            if (result == AcceptTxResult.Accepted) Interlocked.Increment(ref _accepted);
            else if (result == AcceptTxResult.FrameSimulationFailed) Interlocked.Increment(ref _failed);
            else if (result != AcceptTxResult.FrameSimulationDeferred) Interlocked.Increment(ref _other);
            else if (result.ToString().Contains("budget", StringComparison.Ordinal)) Interlocked.Increment(ref _deferredBudget);
            else if (result.ToString().Contains("preempted", StringComparison.Ordinal)) Interlocked.Increment(ref _deferredPreempted);
            else Interlocked.Increment(ref _deferredBusy);
        }
    }

    private static double Median(List<double> values) => Percentile(values, 0.50);

    private static int Outliers(List<double> samples, double thresholdUs) => samples.Count(v => v > thresholdUs);

    /// <summary>The host's 1-minute load average when the row is written, so a row measured on a busy host shows it.</summary>
    private static string HostLoad1()
    {
        try
        {
            return File.ReadAllText("/proc/loadavg").Split(' ')[0];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "n/a";
        }
    }

    /// <summary>Garbage collection during one measured window, so a victim stall can be told apart from the flood.</summary>
    /// <remarks>Counts are <see cref="GC.CollectionCount"/> deltas: a gen2 collection also counts in gen0 and gen1.</remarks>
    private readonly record struct GcDelta(double PauseMs, int Gen0, int Gen1, int Gen2)
    {
        public readonly record struct Snapshot(TimeSpan Pause, int Gen0, int Gen1, int Gen2);

        public static Snapshot Take() =>
            new(GC.GetTotalPauseDuration(), GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));

        public static GcDelta Since(Snapshot start)
        {
            Snapshot now = Take();
            return new((now.Pause - start.Pause).TotalMilliseconds, now.Gen0 - start.Gen0, now.Gen1 - start.Gen1, now.Gen2 - start.Gen2);
        }

        public string Fields(string prefix) =>
            $"{prefix}gc_pause_ms={PauseMs:F1} {prefix}gc0={Gen0} {prefix}gc1={Gen1} {prefix}gc2={Gen2}";
    }

    /// <summary>A VERIFY frame that approves execution and payment at once: the cheapest valid opaque prefix, so
    /// the pool must simulate it and charges the simulation to the per-head budget.</summary>
    private static byte[] HonestVerifyCode() =>
        Prepare.EvmCode
            .PushData((byte)FrameFlags.ApproveExecutionAndPayment)
            .PushData(0)
            .PushData(0)
            .Op(Instruction.APPROVE)
            .Done;

    private static Address HonestSender(int index) =>
        new(Keccak.Compute($"frame-tx-honest-sender-{index}").Bytes[12..]);

    private static Transaction HonestFrameTx(Address sender)
    {
        Transaction tx = new()
        {
            Type = TxType.FrameTx,
            ChainId = TestBlockchainIds.ChainId,
            Nonce = 0,
            SenderAddress = sender,
            Frames = [new TxFrame(FrameMode.Verify, FrameFlags.ApproveExecutionAndPayment, target: null, gasLimit: HonestVerifyGas, UInt256.Zero, default)],
            FrameSignatures = [],
            GasLimit = (long)HonestVerifyGas,
            GasPrice = 1.GWei,
            DecodedMaxFeePerGas = 1.GWei,
        };
        tx.Hash = tx.CalculateHash();
        return tx;
    }

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
                ProcessImport(_computeBlock);
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

    /// <summary>Adds one block, so the pool and the simulator's per-head budget see a new head.</summary>
    /// <remarks>The block may include honest transactions the pool admitted, as a real proposer's would.</remarks>
    private void AdvanceHead()
    {
        Hash256? before = _chain.BlockTree.Head?.Hash;
        _chain.AddBlock(Nethermind.Core.Test.Blockchain.TestBlockchainUtil.AddBlockFlags.MayHaveExtraTx).GetAwaiter().GetResult();
        Assert.That(_chain.BlockTree.Head?.Hash, Is.Not.EqualTo(before), "the head did not advance");
    }

    /// <summary>Submits flood transactions straight to the pool, or as one-transaction gossip tasks on the node's
    /// <see cref="BackgroundTaskScheduler"/> bound to the chain's main branch processor.</summary>
    /// <remarks>A task whose token is already cancelled when it runs has expired while block processing held the
    /// queue; it is dropped, as <c>HandleSlow</c> drops a message cancelled before its first transaction. A task
    /// already inside <c>SubmitTx</c> when a block starts runs on; the pool preempts its simulation on the processing
    /// flag.</remarks>
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
