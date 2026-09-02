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
/// Measures the campaign's contention metrics: baseline block-processing time <c>W_0</c>, block-processing
/// time under an admission flood <c>W</c>, their difference <c>Δ</c>, and the sustainable adversarial
/// rejection rate <c>R_max</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>FrameTxMempoolDosMeasurement</c> in <c>Nethermind.TxPool.Test</c> measures the other half of the
/// campaign, <c>t_reject</c>, on an idle node. This harness answers what that one cannot: what the unpaid
/// admission work costs a node that is also trying to process blocks on the same core. Both halves are real
/// and independently wired by production modules — <see cref="TestBlockchain.TxPool"/> resolves the real
/// <c>FrameTxPrefixSimulator</c> registered in <c>BlockProcessingModule</c>, and
/// <see cref="TestBlockchain.BranchProcessor"/> is the same block processor the client runs. They are not
/// made to share a world state, because the contention the plan names is for the CPU: the simulator works
/// over its own resettable read-only state exactly as it does in production.
/// </para>
/// <para>
/// The load generator is open-loop: each submission has an absolute deadline computed from the run's start,
/// so a slow simulation delays that submission without shifting every later one — a closed-loop generator
/// would silently throttle itself to the node's capacity and could never show saturation. Achieved rate and
/// worst submission lag are reported alongside every measurement so a saturated run is visible rather than
/// read as a low-delay result.
/// </para>
/// <para>
/// Numbers from a developer machine are indicative only: the plan's setup is a dedicated runner, and CPU
/// frequency scaling alone moves these figures by tens of percent.
/// </para>
/// </remarks>
[TestFixture]
[Explicit("measurement harness")]
[NonParallelizable]
public class FrameTxFloodMeasurement
{
    private const long BlockGasLimit = 30_000_000;

    /// <summary>Ordinary transfers in the block whose processing time is measured.</summary>
    /// <summary>Ordinary transfers in the measured block. Sets <c>W_0</c>, and therefore every <c>Δ</c> and
    /// every <c>delta_p50_pct</c> the campaign reports, so it is the most load-bearing constant here.</summary>
    /// <remarks>
    /// 200 transfers is about 4.2M gas against a 30M limit, so <c>W_0</c> is well below a full block's and
    /// <c>delta_p50_pct</c> is correspondingly larger than it would be against one. That is deliberate: this
    /// is a CPU-contention experiment on one core, so the baseline wants to be CPU work rather than state
    /// growth, and the fraction is the quantity that transfers across machines. Read <c>delta_p50_us</c> for
    /// the absolute cost and treat the percentage as relative to this baseline, not to a mainnet block.
    /// </remarks>
    private const int TransfersPerBlock = 200;

    /// <summary>
    /// How long each measurement collects block-processing samples for.
    /// </summary>
    /// <remarks>
    /// Measurements are bounded by wall clock rather than by an iteration count so that the flood a rate
    /// point describes is actually sustained across the window. A fixed count made the window scale inversely
    /// with block-processing speed, and a 60-iteration window on fast blocks admitted twelve transactions at
    /// 50 tx/s, which is a handful of events rather than a flood.
    /// </remarks>
    private static readonly TimeSpan MeasureWindow = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Discarded block-processing time before every measurement.
    /// </summary>
    /// <remarks>
    /// Tiered JIT promotion of the processing path dominates anything shorter. With a fixed 10-iteration
    /// warmup the same baseline workload drifted from 7,982 µs to 1,777 µs over one fixture run, which is
    /// larger than most of the deltas being measured and produced negative ones.
    /// </remarks>
    private static readonly TimeSpan WarmupWindow = TimeSpan.FromSeconds(4);

    /// <summary>Time the flood runs before sampling starts, so the window measures steady state.</summary>
    /// <summary>Time the generator runs before warmup, so its thread is scheduled and its first-submission
    /// costs are paid outside any measured span. Not derived from a measurement; long enough that the
    /// generator's own startup is not attributed to the node, short enough not to dominate the case.</summary>
    private static readonly TimeSpan FloodSettle = TimeSpan.FromMilliseconds(750);

    /// <summary>
    /// One-off block-processing warmup, paid by whichever case runs first in the fixture.
    /// </summary>
    /// <remarks>
    /// Tiered JIT promotion of the processing path is charged to the first case that exercises it, and the
    /// per-measurement warmup is sized for a warm process. Left unpaid it surfaces as baseline drift large
    /// enough for the drift gate to reject the first case outright.
    /// </remarks>
    private static readonly TimeSpan FixtureWarmupWindow = TimeSpan.FromSeconds(15);

    private static bool _fixtureWarmed;

    /// <summary>Distinct pre-built adversarial transactions, cycled by the generator.</summary>
    /// <remarks>
    /// Must exceed the highest offered rate times the generator's lifetime (<see cref="FloodSettle"/> plus
    /// <see cref="WarmupWindow"/> plus <see cref="MeasureWindow"/>), or the cycle re-offers hashes already in
    /// the head-scoped known cache and those submissions never reach the EVM. The rejected-equals-submitted
    /// assertions catch it, but raising a window or adding a faster rate point should raise this too.
    /// </remarks>
    private const int FloodPoolSize = 4_096;

    private static readonly UInt256 AttackerBalance = 1_000.Ether;

    /// <summary>
    /// The attacker's account. Needs deployed code for <c>FrameTxPayerResolver</c> to return
    /// <c>RequiresSimulation</c>, which is what carries the transaction to the EVM stage.
    /// </summary>
    private static readonly Address Attacker = TestItem.AddressF;

    /// <summary>Funds the ordinary transfers. Deliberately not <c>AddressA</c>, which test genesis gives code.</summary>
    private static readonly PrivateKey BlockSenderKey = TestItem.PrivateKeyB;

    private static readonly Address TransferTarget = TestItem.AddressC;

    /// <summary>
    /// Environment variable naming the directory that holds the Groth16 sweep artifacts.
    /// </summary>
    /// <remarks>
    /// Required rather than defaulted, for the same reason <c>FrameTxMempoolDosMeasurement</c> requires it:
    /// the artifacts are generated outside this repository, so any built-in path is one machine's. Unset,
    /// the Groth16 cases skip.
    /// </remarks>
    private const string Groth16ArtifactRootVariable = "FRAME_GROTH16_ARTIFACTS";

    /// <summary>One Groth16 sweep point: the artifact directory and the ceiling its frame declares.</summary>
    /// <remarks>
    /// Mirrors <c>FrameTxMempoolDosMeasurement.Groth16Sweep</c> without <c>ExpectedFrameGas</c> or the
    /// revert-vs-returns-false <c>Failure</c> distinction: this harness times admission under flood, not the
    /// frame's own gas burn or how it fails, so it has no use for either — the guard it relies on
    /// (<see cref="Admission_flood_actually_reaches_the_simulator"/>) only needs <c>FrameSimulationFailed</c>
    /// to have fired, not which of the two rejection strings did it.
    /// </remarks>
    private readonly record struct Groth16Sweep(string Directory, ulong Ceiling);

    private static readonly Dictionary<string, Groth16Sweep> Groth16Sweeps = new()
    {
        ["groth16-236k"] = new Groth16Sweep("sweep-236k", 236_285),
        ["groth16-300k"] = new Groth16Sweep("sweep-300k", 300_000),
        ["groth16-500k"] = new Groth16Sweep("sweep-500k", 510_000),
        // The plan's named workload, not a synthetic stand-in: soispoke's real spend verifier at ten public
        // signals. Declares 300,000 because its real burn (248,437, per FrameTxMempoolDosMeasurement) clears
        // the stock constant, so this is the one privacy point measurable without raise_verify_gas_const.
        ["groth16-soispoke"] = new Groth16Sweep("sweep-soispoke", 300_000),
    };

    /// <summary>Payload the flood transactions' calldata starts with, before the per-sample salt. Empty for
    /// the synthetic shapes.</summary>
    private byte[] _frameCalldataPrefix = [];

    /// <summary>Signature entries the flood's frame carries. Empty for every shape except signature-stuffed.</summary>
    private TxFrameSignature[] _frameSignatures = [];

    /// <summary>
    /// What the flood's frame declares as its own execution gas limit. Equal to the ceiling under test for
    /// every shape except signature-stuffed, whose frame never executes and only has to clear the entry
    /// charge — for that shape the ceiling is spent via <see cref="_frameSignatures"/> instead, so this is
    /// <see cref="MinimalFrameGas"/>.
    /// </summary>
    private ulong _frameExecutionGasLimit;

    /// <summary>Frame budget for the signature-stuffed shape: well-formed, and small enough that the
    /// declared total is signature gas. The frame never executes, so this only has to clear the 100-gas
    /// entry charge.</summary>
    private const ulong MinimalFrameGas = 400;

    /// <summary>
    /// The most secp256k1 entries <paramref name="ceiling"/> admits once <see cref="MinimalFrameGas"/> is
    /// reserved for the frame. EIP-8141 rule 6 charges
    /// <see cref="Eip8141Constants.Secp256k1VerificationGasCost"/> per entry against the same budget the
    /// validation prefix draws from, so this is the whole ceiling spent on recovery instead of on execution.
    /// </summary>
    private static int StuffedSignatureCount(ulong ceiling) =>
        (int)((ceiling - MinimalFrameGas) / Eip8141Constants.Secp256k1VerificationGasCost);

    private FloodTestBlockchain _chain = null!;
    private BlockHeader _parent = null!;
    private Block _workloadBlock = null!;
    private Transaction[] _floodTxs = null!;

    /// <summary>
    /// Next unused calldata salt. Every rate point draws a fresh range.
    /// </summary>
    /// <remarks>
    /// A rejected frame transaction never enters the pool, but its hash still enters the already-known cache,
    /// which is scoped to the head and the head never moves here. Re-offering a transaction therefore gets it
    /// dropped before the EVM, and a ramp that recycled one fixed pool silently stopped measuring simulation
    /// partway through — reporting block-processing times *below* baseline because the flood had become free.
    /// </remarks>
    private int _saltCursor;

    /// <summary>
    /// The CPUs this process may actually run on, read from the OS rather than asserted.
    /// </summary>
    /// <remarks>
    /// <b>Run this fixture under <c>taskset -c 0</c> for the plan's single-core worst case.</b> Setting
    /// <see cref="Process.ProcessorAffinity"/> from inside the process is not sufficient on Linux: it returns
    /// successfully and reports the new mask, but the measurement threads keep running across every core.
    /// Measured directly, the same four cases showed no flood effect at all under in-process affinity
    /// (3,425 µs against a 3,463 µs baseline, generator sustaining its full offered rate) versus an 83% delay
    /// under <c>taskset</c> (6,945 µs against 3,793 µs, generator falling to 60% of offered) — trusting the
    /// in-process flag means reporting "an admission flood is free" for a node that was never contended. So
    /// this reports what the OS says rather than what was requested: <c>Cpus_allowed_list</c> from
    /// <c>/proc/self/status</c> is authoritative on Linux, elsewhere the process's own view is the best
    /// available, and every result line carries it so a run that was not confined cannot be read as one that was.
    /// </remarks>
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

    /// <summary>
    /// True when the process is confined to exactly one CPU, so the plan's contention actually holds.
    /// </summary>
    /// <remarks>
    /// <c>Cpus_allowed_list</c> is a comma-and-range list such as <c>0-3</c> or <c>0,2</c>. A single CPU is
    /// the only form carrying neither separator.
    /// </remarks>
    private static bool IsSingleCore()
    {
        string set = ObservedCpuSet();

        // Windows reports an affinity bitmask rather than a list, so one allowed CPU is one set bit. Reading
        // it back through ObservedCpuSet keeps the single Process access inside that method's handler.
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

    /// <summary>
    /// Skips unless the process is confined to one CPU, which is the contention the plan specifies.
    /// </summary>
    /// <remarks>
    /// Reporting the CPU set is not enough: an unconfined run produces a complete, plausible, green result
    /// set showing almost no flood effect, which is exactly the wrong conclusion rather than an obvious
    /// failure. Run under <c>taskset -c 0</c>. <c>FRAME_FLOOD_ALLOW_MULTICORE=1</c> opts out for anyone
    /// deliberately measuring the uncontended case.
    /// </remarks>
    private static void SkipUnlessSingleCore()
    {
        if (IsSingleCore() || Environment.GetEnvironmentVariable("FRAME_FLOOD_ALLOW_MULTICORE") == "1") return;

        Assert.Ignore($"this process may run on CPUs [{ObservedCpuSet()}], so the single-core contention this "
                      + "harness measures does not hold and a flood would appear nearly free. Re-run under "
                      + "`taskset -c 0`, or set FRAME_FLOOD_ALLOW_MULTICORE=1 to measure the uncontended case "
                      + "deliberately.");
    }

    /// <summary>
    /// Largest baseline drift, as a percentage, a rate point may show before its delta is untrustworthy.
    /// </summary>
    /// <remarks>
    /// The two idle baselines bracket the flood. Disagreement beyond this means the run drifted — JIT
    /// tiering, frequency scaling, a noisy neighbour — and the delta is that drift as much as it is the flood.
    /// </remarks>
    private const double MaxBaselineDriftPercent = 5.0;

    /// <summary>
    /// Baseline drift above which the run is treated as broken rather than merely untrustworthy.
    /// </summary>
    /// <remarks>
    /// Between this and <see cref="MaxBaselineDriftPercent"/> the row is emitted with <c>valid=no</c> and
    /// left for the reader to discard; above it the two baselines describe different machines and there is
    /// nothing to salvage, so the case fails rather than publishing a delta.
    /// </remarks>
    private const double BrokenBaselineDriftPercent = 25.0;

    /// <summary>
    /// Largest submission lag, in multiples of the inter-arrival period, a rate may show and still count as
    /// sustained.
    /// </summary>
    /// <remarks>
    /// An open-loop generator that falls behind and then fires its backlog satisfies an average-rate test,
    /// while a backlog is precisely what <c>R_max</c> is defined to exclude. Lag survives that averaging.
    /// </remarks>
    private const double MaxSustainedLagPeriods = 5.0;

    /// <summary>Share of the offered rate the generator must still deliver to count as keeping up rather than
    /// falling behind and firing its backlog.</summary>
    private const double RateHeldFloor = 0.95;

    /// <summary>Share of the offered rate the generator must deliver for a saturated run to still describe a
    /// flood at that rate, rather than one starved down to a different, unlabelled load.</summary>
    private const double MinDeliveredRateFloor = 0.25;

    /// <summary>One rate point: what was offered, what landed, and what block processing cost meanwhile.</summary>
    private readonly record struct FloodOutcome(
        double OfferedRate,
        double AchievedRate,
        int Submitted,
        int Rejected,
        double MaxLagUs,
        int QueueGrowth,
        List<double> ProcessMicros);

    /// <remarks>
    /// The fixture is one instance for all its tests, so a case that skips before <see cref="BuildChain"/>
    /// would otherwise leave the previous case's already-disposed chain for teardown to dispose again.
    /// </remarks>
    [SetUp]
    public void Setup()
    {
        _chain = null!;
        _frameCalldataPrefix = [];
        _frameSignatures = [];
    }

    [TearDown]
    public void TearDown() => _chain?.Dispose();

    /// <summary>
    /// The guard every other case in this fixture rests on: that a submitted frame transaction actually
    /// reaches the rejection path it claims to — the EVM-backed simulator for every shape but one, the
    /// signature filter for signature-stuffed — for every shape the flood/ramp cases sweep, not only the
    /// synthetic one.
    /// </summary>
    /// <remarks>
    /// Without this, a flood of transactions dropped by a cheap upstream filter would show a delightful
    /// <c>Δ</c> of nearly zero while measuring nothing at all. No other test in the repository exercises the
    /// simulator that <c>BlockProcessingModule</c> wires into every pool, so its presence is asserted here
    /// rather than assumed. Carries no <see cref="SkipUnlessSingleCore"/> gate: it proves a correctness
    /// property, not a contention one. The signature-stuffed case runs at 500,000 deliberately, not the
    /// stock 300,000 ceiling — proving the property this shape exists to demonstrate, that it is reachable
    /// there without <c>raise_verify_gas_const</c>.
    /// </remarks>
    [TestCase("keccak-wide")]
    [TestCase("groth16-236k")]
    [TestCase("groth16-300k")]
    [TestCase("groth16-500k")]
    [TestCase("groth16-soispoke")]
    [TestCase("signature-stuffed")]
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

    /// <summary>
    /// The plan's <c>W(C, r, K_retry)</c>: block-production time while an admission flood arrives and the
    /// producer is also re-executing a pending prefix that never approves.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The measured operation is a block-<em>production</em> pass, not the block-processing pass the other
    /// cases time, since producer-side re-execution and its eviction bound exist only on the production path.
    /// The retry gate stands in for the pool's eviction decision and resets only the attempt counter: the same
    /// never-approving prefix stays resident, since nothing downstream reads its identity and rebuilding one
    /// would charge the low-<c>K_retry</c> arms a hash the high ones avoid — on the very axis this case
    /// compares. The producer therefore always has exactly one never-approving prefix to burn through,
    /// describing a sustained attack rather than a decaying one.
    /// </para>
    /// <para>
    /// <b>This case cannot answer what it looks like it answers.</b> Per-pass cost is set by how many
    /// never-approving transactions are <em>resident</em>, and the rig pins residency at one whatever
    /// <c>K_retry</c> is: eviction only resets a counter. So a flat result here is a property of the rig, not
    /// of the retry policy. In a real pool a larger <c>K_retry</c> keeps proportionally more of them resident
    /// at once, which is the effect this axis exists to measure and the rig removes. Read the rows as per-pass
    /// latency at fixed occupancy, and do not conclude anything about retry policy from their flatness.
    /// </para>
    /// <para>
    /// The offered rate stays at 100 because the producer alone takes about half the core, leaving a ceiling
    /// near <c>1 / (2 * t_reject(C))</c>: about 135 tx/s at 236,285, where offering 200 delivered 78 and 102
    /// in the two arms and compared loads rather than policies. <c>t_reject</c> rises with the ceiling, so 100
    /// may saturate at 300k and above; <c>flood_achieved_rate</c> and <c>delivered</c> on each row say whether
    /// it did.
    /// </para>
    /// </remarks>
    [TestCase(100_000ul, 1, 0)]
    [TestCase(100_000ul, 2, 0)]
    [TestCase(100_000ul, 4, 0)]
    [TestCase(100_000ul, 8, 0)]
    [TestCase(100_000ul, 1, 100)]
    [TestCase(100_000ul, 2, 100)]
    [TestCase(100_000ul, 4, 100)]
    [TestCase(100_000ul, 8, 100)]
    [TestCase(236_285ul, 1, 0)]
    [TestCase(236_285ul, 8, 0)]
    [TestCase(236_285ul, 1, 100)]
    [TestCase(236_285ul, 8, 100)]
    [TestCase(300_000ul, 1, 0)]
    [TestCase(300_000ul, 8, 0)]
    [TestCase(300_000ul, 1, 100)]
    [TestCase(300_000ul, 8, 100)]
    [TestCase(500_000ul, 1, 0)]
    [TestCase(500_000ul, 8, 0)]
    [TestCase(500_000ul, 1, 100)]
    [TestCase(500_000ul, 8, 100)]
    public async Task Block_production_delay_under_flood_and_retries(ulong ceiling, int kRetry, int offeredRate)
    {
        SkipUnlessSingleCore();
        // Only the flooded arms submit through TxPool/simulation, where CapFrameGas still clamps a stock
        // build; the no-flood arms never reach ExecutionOptions.FrameValidationPrefixOnly at all (the
        // producer path calls Execute() directly), so the const never binds them — proven in-tree by
        // FrameTxVerifyDosMeasurement, which pushes 1,048,576 gas through that same uncapped path.
        if (offeredRate > 0) Eip8141MeasurementGuards.SkipIfCeilingUnreachable(ceiling);
        await BuildChain("keccak-wide", ceiling);

        using ProducerRig rig = ProducerRig.Create(_chain.SpecProvider, kRetry, ceiling);
        FloodOutcome outcome = offeredRate > 0
            ? MeasureProductionUnderFlood(rig, offeredRate)
            : NoFloodProductionOutcome(rig);

        double p50 = Percentile(outcome.ProcessMicros, 0.50);
        double p95 = Percentile(outcome.ProcessMicros, 0.95);
        // p95 alone trims the tail where a missed slot deadline would show. Sample counts here are in the
        // hundreds, so p99 is still an observed value rather than an extrapolation.
        double p99 = Percentile(outcome.ProcessMicros, 0.99);
        bool floodStarved = offeredRate > 0 && outcome.AchievedRate < offeredRate * RateHeldFloor;
        // Emit runs before the assertions, so without this a row the harness then refuses to stand behind is
        // indistinguishable from one that passed. Same predicate as the gate below, so the two cannot drift.
        bool delivered = offeredRate == 0 || outcome.AchievedRate > offeredRate * MinDeliveredRateFloor;

        Emit($"case=production_pass_at_fixed_occupancy ceiling={ceiling} k_retry={kRetry} offered_rate={offeredRate} "
             + $"cpus={ObservedCpuSet()} single_core={(IsSingleCore() ? "yes" : "no")} passes={outcome.ProcessMicros.Count} "
             + $"evictions={rig.EvictionsInWindow} failing_executions={rig.ExecutionsInWindow} "
             + $"flood_submitted={outcome.Submitted} flood_rejected={outcome.Rejected} "
             + $"flood_achieved_rate={outcome.AchievedRate:F1} flood_starved={(floodStarved ? "yes" : "no")} delivered={(delivered ? "yes" : "no")} "
             + $"production_p50_us={p50:F1} production_p95_us={p95:F1} production_p99_us={p99:F1}");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(rig.FailingExecutions, Is.GreaterThan(0),
                "the producer never re-executed the failing prefix, so this measures an ordinary block");
            Assert.That(rig.Evictions, Is.GreaterThan(0),
                $"no eviction fired at K_retry={kRetry}, so the retry bound was never exercised");
            Assert.That(outcome.ProcessMicros, Has.Count.GreaterThan(10),
                "too few production passes for a percentile to mean anything");
            Assert.That(outcome.Rejected, Is.EqualTo(outcome.Submitted).Within(1),
                "flood transactions were dropped before the simulator, so the flood arm measures an idle pool");
            if (offeredRate > 0)
            {
                Assert.That(outcome.Submitted, Is.GreaterThan(10),
                    "the generator barely ran, so any difference against the no-flood arm is not a flood effect");

                // Not an equality with the offered rate: on a saturated core the generator legitimately cannot
                // keep up, and that is a result rather than a fault. It must still deliver a flood worth the
                // name, and flood_achieved_rate on the row says what it actually delivered.
                Assert.That(outcome.AchievedRate, Is.GreaterThan(offeredRate * MinDeliveredRateFloor),
                    $"the generator delivered {outcome.AchievedRate:F1} tx/s against {offeredRate} offered, too far "
                    + "below the label for this row to describe a flood at that rate");
            }
        }
    }

    /// <summary>The no-flood arm, shaped as a <see cref="FloodOutcome"/> so both arms share one Emit/assert path.</summary>
    private static FloodOutcome NoFloodProductionOutcome(ProducerRig rig)
    {
        rig.RunFor(WarmupWindow);
        rig.MarkWindowStart();
        return new FloodOutcome(0, 0, 0, 0, 0, 0, rig.Measure(MeasureWindow));
    }

    /// <summary>
    /// An open-loop generator: submits pre-built transactions on a fixed-rate schedule from a background
    /// thread, counting submissions, simulator rejections, and how far behind schedule each submission ran.
    /// </summary>
    /// <remarks>
    /// Shared by both cases that run a flood concurrently with something else being measured — block
    /// processing/production time on the caller's thread, this generator on its own — so a generator that
    /// cannot get scheduled, or whose transactions are dropped by a cheaper filter, cannot silently read as
    /// "no flood effect" on one case while being caught by the other's assertions.
    /// </remarks>
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
                    // Rejected first: the reader snapshots submitted then rejected, so this order cannot show
                    // more submissions than rejections for a flood in which every submission is rejected.
                    if (result == AcceptTxResult.FrameSimulationFailed) Interlocked.Increment(ref Rejected);
                    Interlocked.Increment(ref Submitted);
                }
            })
            { IsBackground = true, Name = "frame-tx-flood" };

        public void Start() => Thread.Start();

        /// <summary>Zeroes the running lag maximum, so a caller can exclude a settle/warmup phase's spikes
        /// from the window it actually samples.</summary>
        public void ResetMaxLag() => Volatile.Write(ref _maxLagUs, 0);

        public bool Stop(CancellationTokenSource cts)
        {
            cts.Cancel();
            return Thread.Join(TimeSpan.FromSeconds(30));
        }
    }

    /// <summary>
    /// Proves the block whose processing time is measured actually executes its transactions.
    /// </summary>
    /// <remarks>
    /// The whole campaign's <c>Δ</c> is a difference of block-processing times, so a workload block that
    /// silently included nothing would make every delay figure a measurement of an empty loop. Nothing else
    /// here would notice: an empty block still processes, still takes time, and still slows down under a
    /// flood.
    /// </remarks>
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

    /// <summary>
    /// The campaign's <c>Δ</c>: how much a sustained admission flood adds to block-processing time, per
    /// ceiling and offered rate.
    /// </summary>
    /// <remarks>
    /// The ceiling axis is the point of the sweep. 500k exceeds <see cref="Eip8141Constants.MaxVerifyGas"/>
    /// on this branch and is skipped rather than silently clamped down to the constant and reported under a
    /// 500k label.
    /// </remarks>
    [TestCase(100_000ul, 50)]
    [TestCase(100_000ul, 100)]
    [TestCase(100_000ul, 150)]
    [TestCase(100_000ul, 200)]
    [TestCase(236_285ul, 50)]
    [TestCase(236_285ul, 100)]
    [TestCase(236_285ul, 150)]
    [TestCase(236_285ul, 200)]
    [TestCase(300_000ul, 50)]
    [TestCase(300_000ul, 100)]
    [TestCase(300_000ul, 150)]
    [TestCase(300_000ul, 200)]
    [TestCase(500_000ul, 50)]
    [TestCase(500_000ul, 100)]
    [TestCase(500_000ul, 150)]
    [TestCase(500_000ul, 200)]
    public async Task Block_processing_delay_under_admission_flood(ulong ceiling, int offeredRate) =>
        await MeasureFloodDelay("keccak-wide", ceiling, offeredRate);

    /// <summary>The privacy workload's <c>Δ</c>: the same delay measurement, flooded with the Groth16 shape
    /// instead of the synthetic one.</summary>
    [TestCase("groth16-236k", 50)]
    [TestCase("groth16-236k", 100)]
    [TestCase("groth16-236k", 150)]
    [TestCase("groth16-236k", 200)]
    [TestCase("groth16-300k", 50)]
    [TestCase("groth16-300k", 100)]
    [TestCase("groth16-300k", 150)]
    [TestCase("groth16-300k", 200)]
    [TestCase("groth16-500k", 50)]
    [TestCase("groth16-500k", 100)]
    [TestCase("groth16-500k", 150)]
    [TestCase("groth16-500k", 200)]
    [TestCase("groth16-soispoke", 50)]
    [TestCase("groth16-soispoke", 100)]
    [TestCase("groth16-soispoke", 150)]
    [TestCase("groth16-soispoke", 200)]
    public async Task Block_processing_delay_under_admission_flood_groth16(string shape, int offeredRate) =>
        await MeasureFloodDelay(shape, Groth16Sweeps[shape].Ceiling, offeredRate);

    /// <summary>
    /// The worst known adversarial shape's <c>Δ</c>: the plan asks to replace the adversarial baseline "if
    /// benchmarking identifies a more expensive CPU-per-gas shape", and <c>FrameTxMempoolDosMeasurement</c>'s
    /// <c>t_reject</c> data already shows signature-stuffing costs ~16% more per gas than <c>keccak-wide</c>
    /// at 300k. Run alongside <c>keccak-wide</c> rather than replacing it, so both are on record.
    /// </summary>
    /// <remarks>
    /// No delivered-rate floor here (unlike <see cref="Block_production_delay_under_flood_and_retries"/>'s
    /// <c>MinDeliveredRateFloor</c>): this method already reports <c>saturated=</c> rather than asserting on
    /// it, for every shape. Signature-stuffing costs ~4,950 µs at 300k (<c>1 / t_reject ≈ 200 tx/s</c>), so
    /// the <c>offeredRate=200</c> row sits close to the saturation point and may show <c>achieved_rate</c>
    /// well under the label — expected, and visible on the row rather than hidden by it.
    /// </remarks>
    [TestCase(100_000ul, 50)]
    [TestCase(100_000ul, 100)]
    [TestCase(100_000ul, 150)]
    [TestCase(100_000ul, 200)]
    [TestCase(236_285ul, 50)]
    [TestCase(236_285ul, 100)]
    [TestCase(236_285ul, 150)]
    [TestCase(236_285ul, 200)]
    [TestCase(300_000ul, 50)]
    [TestCase(300_000ul, 100)]
    [TestCase(300_000ul, 150)]
    [TestCase(300_000ul, 200)]
    [TestCase(500_000ul, 50)]
    [TestCase(500_000ul, 100)]
    [TestCase(500_000ul, 150)]
    [TestCase(500_000ul, 200)]
    public async Task Block_processing_delay_under_admission_flood_signature_stuffed(ulong ceiling, int offeredRate) =>
        await MeasureFloodDelay("signature-stuffed", ceiling, offeredRate);

    private async Task MeasureFloodDelay(string shape, ulong ceiling, int offeredRate)
    {
        SkipUnlessSingleCore();
        // Not for signature-stuffed: CapFrameGas never binds a rejection that happens before simulation.
        if (shape != "signature-stuffed") Eip8141MeasurementGuards.SkipIfCeilingUnreachable(ceiling);
        await BuildChain(shape, ceiling);

        List<double> baseline = MeasureBlockProcessing(MeasureWindow, WarmupWindow);
        FloodOutcome flooded = MeasureUnderFlood(offeredRate, RejectionCounterFor(shape));

        // A second idle baseline after the flood. If the two disagree materially the run drifted — from JIT
        // tiering, frequency scaling or a noisy neighbour — and Δ is that drift as much as it is the flood.
        List<double> baselineAfter = MeasureBlockProcessing(MeasureWindow, TimeSpan.Zero);

        double w0 = Percentile(baseline, 0.50);
        double w = Percentile(flooded.ProcessMicros, 0.50);
        double w0p95 = Percentile(baseline, 0.95);
        double wp95 = Percentile(flooded.ProcessMicros, 0.95);
        double w0p99 = Percentile(baseline, 0.99);
        double wp99 = Percentile(flooded.ProcessMicros, 0.99);
        double w0After = Percentile(baselineAfter, 0.50);
        double baselineDriftPct = w0 <= 0 ? 0 : Math.Abs(w0After - w0) / w0 * 100;

        Emit($"case=flood_delay shape={shape} ceiling={ceiling} cpus={ObservedCpuSet()} single_core={(IsSingleCore() ? "yes" : "no")} "
             + $"W0_after_p50_us={w0After:F1} baseline_drift_pct={baselineDriftPct:F1} "
             + $"valid={(baselineDriftPct < MaxBaselineDriftPercent ? "yes" : "no")} "
             + $"offered_rate={offeredRate} achieved_rate={flooded.AchievedRate:F1} "
             + $"submitted={flooded.Submitted} rejected={flooded.Rejected} max_lag_us={flooded.MaxLagUs:F0} "
             + $"queue_growth={flooded.QueueGrowth} "
             + $"saturated={(flooded.AchievedRate < offeredRate * RateHeldFloor ? "yes" : "no")} "
             + $"delta_per_achieved_tx_us={(flooded.AchievedRate > 0 ? (w - w0) / flooded.AchievedRate : 0):F2} "
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
            Assert.That(flooded.Rejected, Is.EqualTo(flooded.Submitted).Within(1),
                "flood transactions were dropped before the simulator, so this measures an idle pool");
            Assert.That(baseline, Has.Count.GreaterThan(10),
                "the baseline window collected too few samples for a percentile to mean anything");
            Assert.That(flooded.Submitted, Is.GreaterThan(10),
                "too few transactions landed inside the sampled window for this to be a sustained flood");
        }
    }

    /// <summary>
    /// The campaign's <c>R_max</c>: the highest offered rate the node absorbs while still keeping up, found by
    /// ramping the rate until the generator can no longer place its submissions on schedule.
    /// </summary>
    /// <remarks>
    /// Reported without reference to <c>B</c>, which the team has not fixed. This is the saturation half of
    /// <c>R_max</c>'s definition — the rate above which a backlog builds — and it is measurable now; the other
    /// half, the highest rate keeping <c>Δ ≤ B</c>, follows from the <c>Δ</c> table once <c>B</c> exists.
    /// Admission is serialised by design: <c>FrameTxPrefixSimulator</c> holds one lock around the whole EVM
    /// run, so a single-threaded generator saturates at roughly <c>1 / t_reject</c> and the ramp is expected to
    /// confirm that rather than discover something larger. 100k is the plan's control point: without it
    /// <c>R_max</c> at the higher ceilings has nothing to be compared against, and the incremental cost of
    /// raising the ceiling cannot be stated. Expect <c>censored=yes</c> there, since <c>1 / t_reject</c> is
    /// well above the grid's top rate and a lower bound is the honest result.
    /// </remarks>
    [TestCase(100_000ul)]
    [TestCase(236_285ul)]
    [TestCase(300_000ul)]
    [TestCase(500_000ul)]
    public async Task Sustainable_rejection_rate_by_ramp(ulong ceiling) =>
        await MeasureSustainableRate("keccak-wide", ceiling);

    /// <summary>The privacy workload's <c>R_max</c>: the same ramp, flooded with the Groth16 shape instead
    /// of the synthetic one.</summary>
    [TestCase("groth16-236k")]
    [TestCase("groth16-300k")]
    [TestCase("groth16-500k")]
    [TestCase("groth16-soispoke")]
    public async Task Sustainable_rejection_rate_by_ramp_groth16(string shape) =>
        await MeasureSustainableRate(shape, Groth16Sweeps[shape].Ceiling);

    /// <summary>The worst known adversarial shape's <c>R_max</c> — see
    /// <see cref="Block_processing_delay_under_admission_flood_signature_stuffed"/> for why it's swept
    /// alongside <c>keccak-wide</c> rather than in place of it.</summary>
    /// <remarks>
    /// At 500,000, signature-stuffing is ~8,250 µs uncontended (~121 tx/s), less under contention — the
    /// closest of any case in this fixture to failing to clear even the ramp's first rate point (50 tx/s)
    /// and tripping <c>Assert.That(lastSustained, Is.GreaterThan(0))</c>.
    /// </remarks>
    [TestCase(100_000ul)]
    [TestCase(236_285ul)]
    [TestCase(300_000ul)]
    [TestCase(500_000ul)]
    public async Task Sustainable_rejection_rate_by_ramp_signature_stuffed(ulong ceiling) =>
        await MeasureSustainableRate("signature-stuffed", ceiling);

    private async Task MeasureSustainableRate(string shape, ulong ceiling)
    {
        SkipUnlessSingleCore();
        // Not for signature-stuffed: CapFrameGas never binds a rejection that happens before simulation.
        if (shape != "signature-stuffed") Eip8141MeasurementGuards.SkipIfCeilingUnreachable(ceiling);
        await BuildChain(shape, ceiling);

        // The baseline is reused by every rate point, so one still descending the JIT tiers would surface as
        // a negative delta at low rates.
        RunFor(WarmupWindow);
        List<double> baseline = MeasureBlockProcessing(MeasureWindow, WarmupWindow);
        double w0 = Percentile(baseline, 0.50);

        Func<long>? rejectionCounter = RejectionCounterFor(shape);
        RunRateRamp(ceiling, shape, "rate_ramp", "r_max", extraFields: "", w0,
            rate => MeasureUnderFlood(rate, rejectionCounter));
    }

    /// <summary>The metrics-delta rejection counter for <paramref name="shape"/>, or <see langword="null"/>
    /// for shapes <see cref="AcceptTxResult.FrameSimulationFailed"/> already identifies unambiguously.
    /// </summary>
    private static Func<long>? RejectionCounterFor(string shape) =>
        shape == "signature-stuffed"
            ? () => Nethermind.TxPool.Metrics.PendingTransactionsFrameTxSignatureInvalid
            : null;

    /// <summary>
    /// <c>R_max(C, K_retry)</c>: the campaign's sustainable-rate ramp, crossed with producer-side retry
    /// contention — the counterpart to <see cref="Sustainable_rejection_rate_by_ramp"/> on the production
    /// path, the way <see cref="Block_production_delay_under_flood_and_retries"/> is the counterpart to
    /// <see cref="Block_processing_delay_under_admission_flood"/>.
    /// </summary>
    /// <remarks>
    /// Held at <c>K_retry</c> ∈ {1, 8} for 236,285/300,000/500,000 rather than the full {1, 2, 4, 8} sweep
    /// <see cref="FrameTxProducerRetryMeasurement.ProducerRetriesAreBoundedByKRetry"/> uses: this case pays a
    /// full rate ramp per point, so the extremes are what a first pass affords elsewhere. 100,000 gets the
    /// full sweep because it is the plan's control point, the one ceiling every other point's cost is read
    /// against — see <see cref="Sustainable_rejection_rate_by_ramp"/> — so its <c>K_retry</c> axis is filled
    /// in completely rather than only at the extremes. On this rig the ramp's 50 tx/s grid is coarse against
    /// admission serialised behind one lock, so a single run's <c>R_max</c> here can land on either of two
    /// adjacent grid points; read it as a range, not a point estimate, until it is measured on the pinned
    /// single-core runner this harness is designed for.
    /// </remarks>
    [TestCase(100_000ul, 1)]
    [TestCase(100_000ul, 2)]
    [TestCase(100_000ul, 4)]
    [TestCase(100_000ul, 8)]
    [TestCase(236_285ul, 1)]
    [TestCase(236_285ul, 8)]
    [TestCase(300_000ul, 1)]
    [TestCase(300_000ul, 8)]
    [TestCase(500_000ul, 1)]
    [TestCase(500_000ul, 8)]
    public async Task Sustainable_rejection_rate_by_ramp_with_retries(ulong ceiling, int kRetry)
    {
        SkipUnlessSingleCore();
        Eip8141MeasurementGuards.SkipIfCeilingUnreachable(ceiling);
        await BuildChain("keccak-wide", ceiling);

        using ProducerRig rig = ProducerRig.Create(_chain.SpecProvider, kRetry, ceiling);
        rig.RunFor(WarmupWindow);
        double w0 = Percentile(rig.Measure(MeasureWindow), 0.50);

        RunRateRamp(ceiling, "keccak-wide", "rate_ramp_with_retries", "r_max_with_retries", $"k_retry={kRetry} ", w0,
            rate => MeasureProductionUnderFlood(rig, rate));
    }

    /// <summary>
    /// Ramps offered rate until a point fails to sustain, emitting one <c>RESULT</c> row per rate point plus
    /// a summary row — shared by the block-processing and block-production ramps, which differ only in what
    /// <paramref name="measureAtRate"/> samples against the flood.
    /// </summary>
    /// <remarks>
    /// Fine enough to locate the knee; a coarser grid reports the last sustained point, not the ceiling.
    /// Reported without reference to <c>B</c>, which the team has not fixed: this is the saturation half of
    /// <c>R_max</c>'s definition — the rate above which a backlog builds — and it is measurable now; the other
    /// half, the highest rate keeping <c>Δ ≤ B</c>, follows from the <c>Δ</c> table once <c>B</c> exists.
    /// </remarks>
    private void RunRateRamp(
        ulong ceiling, string shape, string rateCase, string summaryCase, string extraFields, double w0,
        Func<int, FloodOutcome> measureAtRate)
    {
        // The plan leaves the rate grid open. 50 tx/s steps to 400 brackets saturation at 236,285 and above,
        // where 1/t_reject is roughly 130 to 230 tx/s. It does NOT reach it at 100,000, where 1/t_reject is
        // near 700, so that point reports censored=yes and r_max_upper=unbounded rather than a located value.
        // Raising the top costs a rate point per ceiling; do it if the control point's R_max has to be a
        // number rather than a bound.
        int[] rates = [50, 100, 150, 200, 250, 300, 350, 400];
        double lastSustained = 0;
        bool sustainedEveryRate = true;
        double firstFailedRate = 0;

        foreach (int rate in rates)
        {
            FloodOutcome outcome = measureAtRate(rate);

            // Both conditions, because either alone is satisfiable by a node that is not keeping up: the rate
            // test by a generator firing its backlog, the lag test by one that never got going.
            double periodUs = 1_000_000.0 / rate;
            bool rateHeld = outcome.AchievedRate >= rate * RateHeldFloor;
            bool lagBounded = outcome.MaxLagUs <= periodUs * MaxSustainedLagPeriods;

            // An Undecided simulation outcome admits the transaction, so a growing pool is both a backlog by
            // the plan's definition and a sign the flood stopped being the workload it claims.
            bool queueStable = outcome.QueueGrowth == 0;
            bool sustained = rateHeld && lagBounded && queueStable;
            double w = Percentile(outcome.ProcessMicros, 0.50);

            Emit($"case={rateCase} shape={shape} ceiling={ceiling} {extraFields}cpus={ObservedCpuSet()} single_core={(IsSingleCore() ? "yes" : "no")} offered_rate={rate} "
                 + $"achieved_rate={outcome.AchievedRate:F1} sustained={(sustained ? "yes" : "no")} "
                 + $"max_lag_us={outcome.MaxLagUs:F0} lag_budget_us={periodUs * MaxSustainedLagPeriods:F0} "
                 + $"rate_held={(rateHeld ? "yes" : "no")} lag_bounded={(lagBounded ? "yes" : "no")} "
                 + $"queue_stable={(queueStable ? "yes" : "no")} "
                 + $"submitted={outcome.Submitted} queue_growth={outcome.QueueGrowth} "
                 + $"W0_p50_us={w0:F1} W_p50_us={w:F1} delta_p50_us={w - w0:F1}");

            Assert.That(outcome.Rejected, Is.EqualTo(outcome.Submitted).Within(1),
                $"at {rate} tx/s only {outcome.Rejected} of {outcome.Submitted} submissions reached the "
                + "simulator; the rest were dropped upstream, so this point measures an idle node");

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

        // A ramp whose top rate still sustained has not found R_max, only a lower bound on it.
        // The fact wanted is "the loop never took the break", which is observable. Inferring it from
        // |lastSustained - rates[^1]| < 5% shares a boundary with the sustained test, which needs
        // AchievedRate >= 95% of the offered rate: a top point sustaining at exactly 380.0 of 400 satisfies
        // both and was reported as a located R_max rather than the lower bound it is.
        bool censored = sustainedEveryRate;

        // The ramp stops at the first failure, so the true R_max lies in [lastSustained, firstFailed).
        // Publishing the lower end alone reads as a located value; both ends say what was actually learned.
        double rMaxUpper = censored ? double.PositiveInfinity : firstFailedRate;

        Emit($"case={summaryCase} shape={shape} ceiling={ceiling} {extraFields}cpus={ObservedCpuSet()} single_core={(IsSingleCore() ? "yes" : "no")} "
             + $"r_max_sustained_tx_per_s={lastSustained:F1} r_max_lower={lastSustained:F1} "
             + $"r_max_upper={(censored ? "unbounded" : rMaxUpper.ToString("F1"))} "
             + $"censored={(censored ? "yes" : "no")} "
             + $"basis=no_backlog_and_bounded_lag note=B_not_fixed");

        Assert.That(lastSustained, Is.GreaterThan(0),
            "the node sustained none of the offered rates, so the ramp's lowest point is already saturated");
    }

    /// <summary>
    /// Times block processing repeatedly against a fixed parent for a fixed wall-clock window.
    /// </summary>
    /// <remarks>
    /// Each call re-processes the same block against the same parent state root, so every iteration is
    /// independent and the series is a distribution rather than a trend.
    /// </remarks>
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

    /// <summary>
    /// Runs the open-loop flood on a background thread while block processing is timed on this one.
    /// </summary>
    /// <param name="rejectionCounter">
    /// Reads a Metrics counter attributing rejections to a specific filter in bulk, across the sampled
    /// window, instead of trusting the generator's own per-call tally. Needed for signature-stuffed:
    /// <c>FrameTxSignatureFilter</c> returns the generic <see cref="AcceptTxResult.Invalid"/>, not a
    /// dedicated value, so a per-call check cannot attribute a rejection to that filter specifically — and
    /// a weaker <c>!= Accepted</c> tally would let <c>flood_rejected == flood_submitted</c> pass even if the
    /// whole flood died at a cheaper upstream filter, which is exactly the failure this guards against.
    /// <see langword="null"/> for the two shapes <see cref="AcceptTxResult.FrameSimulationFailed"/> already
    /// identifies unambiguously.
    /// </param>
    private FloodOutcome MeasureUnderFlood(int offeredRate, Func<long>? rejectionCounter = null) =>
        MeasureUnderFloodGeneric(offeredRate,
            warmup: () => { RunFor(FloodSettle); RunFor(WarmupWindow); },
            measure: window => MeasureBlockProcessing(window, TimeSpan.Zero),
            rejectionCounter);

    /// <summary>
    /// Runs the open-loop flood on a background thread while a producer that is also re-executing a
    /// never-approving prefix is measured on this one — the production-path counterpart to
    /// <see cref="MeasureUnderFlood"/>.
    /// </summary>
    private FloodOutcome MeasureProductionUnderFlood(ProducerRig rig, int offeredRate) =>
        MeasureUnderFloodGeneric(offeredRate,
            warmup: () => { Thread.Sleep(FloodSettle); rig.RunFor(WarmupWindow); },
            measure: rig.Measure,
            onWindowStart: rig.MarkWindowStart);

    /// <summary>
    /// Runs the open-loop flood while <paramref name="warmup"/> keeps the core busy through the settle and
    /// warmup phases and <paramref name="measure"/> samples the timed window — the flood-orchestration
    /// mechanics shared by every case that runs a flood concurrently with something else, whether that
    /// something is block processing or block production.
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

        // Counters are snapshotted around the sampled window only, so the reported rate describes the
        // period the sampled distribution came from rather than the whole thread lifetime.
        int submittedAtStart = Volatile.Read(ref generator.Submitted);
        int rejectedAtStart = Volatile.Read(ref generator.Rejected);
        long rejectionCounterAtStart = rejectionCounter?.Invoke() ?? 0;
        int pendingAtStart = _chain.TxPool.GetPendingTransactionsCount();

        // Reset rather than differenced: lag is a running maximum, and the settle and warmup phases are the
        // slowest part of the run, so carrying their spikes into the window would understate the sustainable
        // rate — the quantity this gate exists to protect.
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
            // Without this the generator keeps submitting against a chain TearDown is about to dispose, and
            // the real failure is buried under whatever that races into.
            generator.Stop(cts);
            throw;
        }

        long windowEnd = Stopwatch.GetTimestamp();
        int submittedInWindow = Volatile.Read(ref generator.Submitted) - submittedAtStart;
        int rejectedInWindow = rejectionCounter is null
            ? Volatile.Read(ref generator.Rejected) - rejectedAtStart
            : (int)(rejectionCounter() - rejectionCounterAtStart);
        int queueGrowth = _chain.TxPool.GetPendingTransactionsCount() - pendingAtStart;

        Assert.That(generator.Stop(cts), Is.True,
            "the generator did not stop, so its counters are being read while it still writes them");

        double windowSeconds = (windowEnd - windowStart) / (double)Stopwatch.Frequency;
        double achieved = windowSeconds > 0 ? submittedInWindow / windowSeconds : 0;

        return new FloodOutcome(offeredRate, achieved, submittedInWindow, rejectedInWindow, generator.MaxLagUs, queueGrowth, sampleMicros);
    }

    /// <summary>
    /// Waits for an absolute deadline, yielding the core rather than spinning on it.
    /// </summary>
    /// <remarks>
    /// The generator's waiting is a harness artifact — a real node spends no CPU waiting for the network to
    /// deliver the next transaction — so it must not compete with the block processing being measured. Only
    /// the final approach spins, and on a saturated run the deadline is already past and nothing spins at all.
    /// </remarks>
    private static void WaitUntil(long dueTimestamp, CancellationToken token)
    {
        // Below this the generator yield-spins instead of sleeping, because Thread.Sleep(1) has roughly 1 ms
        // granularity on Linux and the schedule is 2.5 ms apart at the grid's top rate. The spin runs on the
        // same pinned core as the thing being measured, so it is charged to Δ: at 200 tx/s that is up to 4% of
        // the core, largest at the low rates where Δ is smallest. Lowering it trades schedule accuracy, and so
        // max_lag_us and R_max, against that contamination.
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
    /// Builds a chain whose genesis carries the attacker's burn code, plus the block and transaction sets the
    /// measurement reuses.
    /// </summary>
    private async Task BuildChain(string shape, ulong ceiling)
    {
        byte[] attackCode = LoadAttackCode(shape, ceiling);

        // Disabled (0) for every shape except signature-stuffed, which needs the precheck configured at the
        // ceiling under test instead — see FloodTestBlockchain's remarks.
        ulong verifyGasCeiling = shape == "signature-stuffed" ? ceiling : 0;
        _chain = await FloodTestBlockchain.CreateFlood(verifyGasCeiling, builder =>
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

    /// <summary>
    /// Proves the attacker's code reached the state the simulator reads. Its absence is silent: a codeless
    /// target runs EIP-8141 default verify code, which reverts on the empty signature list in microseconds and
    /// reports the same rejection an exhausted budget does.
    /// </summary>
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

    /// <summary>
    /// Pre-builds the flood, so signing, hashing and allocation stay off the timed submission path.
    /// </summary>
    /// <remarks>
    /// The nonce cannot vary: a rejected transaction never enters the pool, so every sample must still be the
    /// attacker's next nonce or the gap-nonce filter would reject it before the EVM. Distinctness comes from a
    /// salt in the frame's calldata, without which the already-known filter would short-circuit the flood
    /// after its first transaction.
    /// </remarks>
    private Transaction[] BuildFloodTransactions(int saltBase)
    {
        Transaction[] txs = new Transaction[FloodPoolSize];
        for (int i = 0; i < txs.Length; i++) txs[i] = FloodFrameTx(saltBase + i);
        return txs;
    }

    /// <summary>
    /// The flood generator's transaction: <see cref="_frameCalldataPrefix"/> followed by the per-sample salt,
    /// so a Groth16 payload keeps its selector and arguments byte-for-byte and the verifier ignores the
    /// trailing surplus, declaring <see cref="_frameExecutionGasLimit"/> and carrying
    /// <see cref="_frameSignatures"/> — both set by <see cref="LoadAttackCode"/> for the shape under test.
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

    /// <summary>The single fixed-shape frame transaction <see cref="ProducerRig"/> reuses every pass. Never
    /// carries a calldata prefix: the producer-retry cases do not sweep the Groth16 shape.</summary>
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

    /// <summary>Memory-expanding hashing loop: the plan's adversarial baseline, KECCAK256 over 4 KiB.</summary>
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
        // The signature-stuffed shape's placeholder body: never executed (refused at the signature filter
        // before simulation), so any code works. Chosen over keccak-wide deliberately — if filter ordering
        // ever changes, this fails loudly at three ops instead of silently burning budget and producing a
        // plausible-looking wrong number.
        "banned-opcode" => Prepare.EvmCode
            .Op(Instruction.TIMESTAMP)
            .Op(Instruction.POP)
            .Op(Instruction.STOP)
            .Done,
        _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, "unknown prefix shape")
    };

    /// <summary>
    /// Resolves the attacker's code for <paramref name="shape"/> at <paramref name="ceiling"/>, setting
    /// <see cref="_frameCalldataPrefix"/>, <see cref="_frameSignatures"/> and
    /// <see cref="_frameExecutionGasLimit"/> as a side effect — empty/ceiling for the synthetic shapes, the
    /// shipped invalid Groth16 payload for the privacy ones, and a stuffed signature list at the minimal
    /// frame budget for signature-stuffed.
    /// </summary>
    /// <returns>The runtime bytecode to seed as the attacker's code.</returns>
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

    /// <summary>
    /// <paramref name="count"/> secp256k1 entries, every one of which the pool fully recovers. The last signs
    /// a different digest, so recovery runs and only then fails the signer compare; a wrong length or a
    /// non-canonical <c>s</c> would be refused before any curve work and measure nothing.
    /// </summary>
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

    /// <summary>
    /// Reads one Groth16 artifact file at run time, without re-encoding it: <c>calldata-invalid.hex</c>
    /// already carries the 4-byte selector, which differs per sweep point because the signature carries the
    /// input count.
    /// </summary>
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

    /// <summary>The Groth16 artifact directory, or a skip when the campaign's artifacts were not supplied.</summary>
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

    /// <summary>Nearest-rank percentile, so a reported figure is an observation that actually occurred.</summary>
    private static double Percentile(List<double> values, double quantile)
    {
        if (values.Count == 0) return double.NaN;
        List<double> sorted = [.. values];
        sorted.Sort();
        int rank = (int)Math.Ceiling(quantile * sorted.Count);
        return sorted[Math.Clamp(rank, 1, sorted.Count) - 1];
    }

    /// <summary>
    /// A test chain whose pool's declared-gas precheck is configured per shape, not fixed.
    /// </summary>
    /// <remarks>
    /// <c>ITxPoolConfig.FrameTxMaxVerifyGas</c> defaults to 300,000 and gates filter stage 7, well before the
    /// EVM. Left at its default, every ceiling above it is rejected on declared gas alone and never reaches
    /// the simulator, which is a different measurement wearing this ceiling's label. For the shapes that need
    /// to reach the simulator above the const, <paramref name="verifyGasCeiling"/> is <c>0</c>, lifting the
    /// precheck entirely — the processor still caps each prefix frame at the
    /// <see cref="Eip8141Constants.MaxVerifyGas"/> <c>const</c>, which is what
    /// <see cref="Eip8141MeasurementGuards.SkipIfCeilingUnreachable"/> guards. For signature-stuffed, which
    /// never reaches the simulator at all, disabling the precheck globally would describe a node no operator
    /// runs (a real node keeps the 300,000 default, fourteen filters ahead of the signature filter, and
    /// refuses a high-declared-gas transaction for free) — so that shape configures the precheck at the
    /// ceiling under test instead, the same fix <c>FrameTxMempoolDosMeasurement</c> already applies.
    /// </remarks>
    /// <param name="verifyGasCeiling">The <c>ITxPoolConfig.FrameTxMaxVerifyGas</c> value for this chain — an
    /// operator-configured ceiling, or <c>0</c> to disable the precheck.</param>
    private sealed class FloodTestBlockchain : BasicTestBlockchain
    {
        private ulong _verifyGasCeiling;

        public static async Task<FloodTestBlockchain> CreateFlood(
            ulong verifyGasCeiling, Action<ContainerBuilder>? configurer = null)
        {
            FloodTestBlockchain chain = new() { _verifyGasCeiling = verifyGasCeiling };
            await chain.Build(configurer);
            return chain;
        }

        protected override IEnumerable<IConfig> CreateConfigs() =>
            [new BlocksConfig { MinGasPrice = 0 }, new TxPoolConfig { FrameTxMaxVerifyGas = _verifyGasCeiling }];
    }

    /// <summary>
    /// A block producer over its own world state, repeatedly offered one never-approving frame transaction
    /// that is evicted after <c>K_retry</c> failed attempts and immediately replaced.
    /// </summary>
    /// <remarks>
    /// Its own state rather than the chain's, for the same reason the flood and the block processor do not
    /// share one: what the plan puts on a single core is CPU, and entangling two world states would add lock
    /// contention that production does not have.
    /// </remarks>
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

        /// <summary>Retry counters scoped to the sampled window, the way the flood counters already are.</summary>
        /// <remarks>
        /// The lifetime counters start at construction and span the settle and warmup phases, so dividing one
        /// by <c>passes</c>, which the row scopes to the window, gives a re-executions-per-pass figure that is
        /// wrong in both directions: 2.68 at <c>K_retry=1</c> where the true value is 1, and 1.94 at
        /// <c>K_retry=8</c> where it is 8. The lifetime values stay for the assertions, which only ask whether
        /// the mechanism fired at all.
        /// </remarks>
        public int EvictionsInWindow => Evictions - _evictionsAtWindowStart;

        public int ExecutionsInWindow => FailingExecutions - _executionsAtWindowStart;

        public void MarkWindowStart()
        {
            _evictionsAtWindowStart = Evictions;
            _executionsAtWindowStart = FailingExecutions;
        }

        /// <summary>Transactions the executor actually handed to the processor, not passes attempted.</summary>
        /// <remarks>
        /// A pass counter cannot detect the failure this guards: a producer that stops re-executing the
        /// prefix would still run its passes, so the assertion would hold while the measurement described an
        /// ordinary empty block.
        /// </remarks>
        public int FailingExecutions => _adapter.Attempts;

        private ProducerRig(IDisposable stateScope, IReleaseSpec spec, ulong ceiling, int kRetry)
        {
            _stateScope = stateScope;
            _spec = spec;
            _kRetry = kRetry;
            _receiptsTracer.SetOtherTracer(NullBlockTracer.Instance);

            // Built once, ahead of the timed window. WithTransactions computes a transaction root that
            // ProcessTransactions then computes again, and a producer does not rebuild its block inside the
            // pass being measured. Safe to reuse: the block is not a BlockToProduce, so ProcessTransactions
            // never replaces its transaction list and only rewrites TxRoot, with the same empty root each pass.
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
            // Counted but not probed: this adapter's executions are inside the timed span.
            CountingAdapter adapter = new(new BuildUpTransactionProcessorAdapter(processor), measureBurn: false);

            // One instance, wired in two phases: the eviction gate closes over the rig that is returned, so
            // the attempt counter it drives is the same one ProduceOnce reads.
            ProducerRig rig = new(scope, spec, ceiling, kRetry);

            IBlockAccessListManager balManager = Substitute.For<IBlockAccessListManager>();
            balManager.Enabled.Returns(false);

            // Stands in for the pool's eviction decision, which is the only thing that ends a retry series:
            // a prefix that never approves never pays, so its nonce never advances and nothing else drops it.
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

        /// <summary>Counts an attempt and reports whether this one exhausts the retry bound.</summary>
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

        private void ProduceOnce()
        {
            _receiptsTracer.StartNewBlockTrace(_block);
            _executor.SetBlockExecutionContext(new BlockExecutionContext(_block.Header, _spec));
            _executor.ProcessTransactions(_block, ProcessingOptions.ProducingBlock, _receiptsTracer, CancellationToken.None);
            _receiptsTracer.EndBlockTrace();

            // Every pass now does identical work whatever K_retry is, which matters because that is the axis
            // this case compares: building a replacement transaction cost the K_retry = 1 arm a hash on every
            // pass and the K_retry = 8 arm one on every eighth. Nothing downstream reads the transaction's
            // identity, so eviction only has to reset the counter.
            if (_attemptsOnCurrent >= _kRetry)
            {
                Evictions++;
                _attemptsOnCurrent = 0;
            }
        }

        /// <remarks>
        /// Only the scope handle: <c>TestWorldStateFactory.CreateForTest</c> keeps its trie store and db
        /// provider internal and returns neither, and <see cref="IWorldState"/> is not disposable, so there
        /// is nothing else here to release.
        /// </remarks>
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
