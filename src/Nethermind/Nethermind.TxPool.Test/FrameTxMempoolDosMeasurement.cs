// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Spec;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Db;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State;
using NUnit.Framework;

namespace Nethermind.TxPool.Test;

/// <summary>
/// Measures <c>t_reject</c>: the wall-clock time a node spends validating and rejecting one invalid
/// EIP-8141 frame transaction at mempool admission, reported as p50 and p99 over raw samples.
/// </summary>
/// <remarks>
/// <para>
/// This is the only harness in the repository that runs a real EVM-backed
/// <see cref="IFrameTxPrefixSimulator"/> inside a real <see cref="TxPool"/>; every other test passes a
/// mock, so the EVM never runs and the expensive half of admission is never timed.
/// <c>Nethermind.Evm.Test/FrameTxVerifyDosMeasurement</c> times the right work at the wrong layer (raw
/// <see cref="ITransactionProcessor"/>, no pool, no filters), and <see cref="FrameTxPrefixRetryMeasurement"/>
/// exercises the right layer with no simulator wired at all.
/// </para>
/// <para>
/// Two spans are timed in the same run: the outer <see cref="TxPool.SubmitTx"/> call, which is the
/// operator-visible rejection latency, and the inner <see cref="IFrameTxPrefixSimulator.Simulate"/> call,
/// which is the EVM stage. Their difference is what admission costs outside the EVM.
/// </para>
/// <para>
/// Every sample is kept. Percentiles are nearest-rank over one sorted array, deliberately not the
/// median-of-medians <c>FrameTxVerifyDosMeasurement</c> reports, which discards exactly the tail a p99
/// is made of.
/// </para>
/// <para>
/// These numbers are not comparable with that harness's. Admission runs the prefix under
/// <c>FrameTxValidationTracer</c>, which traces instructions, stack and storage, so the EVM takes its
/// instrumented specialisation; the Phase 1 harness times the same budget under <c>NullTxTracer</c>. The
/// penalty falls on dispatch-bound shapes, not on shapes whose gas buys real work.
/// </para>
/// <para>
/// Results are appended as <c>RESULT key=value</c> lines to the path in <c>FRAME_MEMPOOL_DOS_OUT</c> (then
/// <c>FRAME_RETRY_OUT</c>, then <c>frame-mempool-dos.txt</c> under the temp directory), because
/// Microsoft.Testing.Platform swallows console writers and nothing written here reaches stdout. The file is
/// appended, so delete it before a run.
/// </para>
/// </remarks>
[TestFixture]
[Explicit("measurement harness")]
[NonParallelizable]
public class FrameTxMempoolDosMeasurement
{
    /// <summary>
    /// Submissions discarded before sampling, enough to promote the instrumented interpreter for this shape.
    /// </summary>
    /// <remarks>
    /// The tiered JIT promotes the traced specialisation only after a shape's opcodes have run enough times,
    /// and charges that promotion to whichever case of the shape runs first — always the lowest ceiling, which
    /// is the sweep's control point. Sized too low, it reports raising the ceiling as making rejection
    /// *cheaper* per gas.
    /// </remarks>
    private const int Warmup = 600;
    private const int Samples = 1_000;
    private const long BlockGasLimit = 30_000_000;
    private const long HeadNumber = 1;
    private const ulong HeadTimestamp = 1_700_000_000;

    /// <summary>
    /// The per-frame budget the processor actually enforces. <see cref="Eip8141Constants.MaxVerifyGas"/> is a
    /// <c>const</c> that <c>ITxPoolConfig.FrameTxMaxVerifyGas</c> does not move, so declaring exactly this much
    /// means the frame is never capped and the whole budget is available to burn.
    /// </summary>
    private const ulong VerifyGas = Eip8141Constants.MaxVerifyGas;

    /// <summary>
    /// The campaign's ceiling sweep, as declared execution gas limits on the synthetic shapes' single frame.
    /// </summary>
    /// <remarks>
    /// <c>CapFrameGas</c> bounds a prefix frame at the lesser of its declared limit and what remains of
    /// <see cref="Eip8141Constants.MaxVerifyGas"/>, so anything at or under the constant needs no source edit
    /// and the declared limit is the budget. <see cref="Ceiling500k"/> is over the constant on this branch and
    /// is skipped unless the constant was raised and the tree rebuilt — a clamped run would otherwise report
    /// the constant's numbers under this ceiling's label. 236,285 is the plan's point, not a round number.
    /// </remarks>
    private const ulong Ceiling100k = 100_000;
    private const ulong Ceiling236k = 236_285;
    private const ulong Ceiling300k = 300_000;
    private const ulong Ceiling500k = 500_000;

    /// <summary>Frame budget for the signature shape: well-formed, and small enough that the declared total is
    /// signature gas. The frame never executes, so this only has to clear the 100-gas entry charge.</summary>
    private const ulong MinimalFrameGas = 400;

    /// <summary>
    /// The most secp256k1 entries <paramref name="ceiling"/> admits once <see cref="MinimalFrameGas"/> is
    /// reserved for the frame. EIP-8141 rule 6 charges
    /// <see cref="Eip8141Constants.Secp256k1VerificationGasCost"/> per entry against the same budget the
    /// validation prefix draws from, so this is the whole ceiling spent on recovery instead of on execution,
    /// which is what makes it comparable to a burn shape at the same ceiling.
    /// </summary>
    private static int StuffedSignatureCount(ulong ceiling) =>
        (int)((ceiling - MinimalFrameGas) / Eip8141Constants.Secp256k1VerificationGasCost);

    /// <summary>Floor below which a per-signature cost is not a real public-key recovery. Measured ~49 us per
    /// entry on the development box, and an isolated <c>EthereumEcdsa.RecoverAddress</c> costs 47 to 51 us, so
    /// 25 clears the slowest plausible machine while still catching a cache with a partial hit rate.</summary>
    private const double MinCredibleRecoveryMicros = 25.0;

    /// <summary>
    /// The share of its granted budget a burn shape must consume for the run to count as a burn.
    /// </summary>
    private const double BudgetBurnFloor = 0.99;

    /// <summary>
    /// How far below its declared ceiling a frame may enter the EVM before the ceiling is treated as clamped.
    /// </summary>
    /// <remarks>
    /// The frame pays its target's account access out of its own limit — 2,600 cold, 100 warm — so entry gas
    /// is legitimately a little under the declared limit. The failure this guards is a <c>CapFrameGas</c> clamp
    /// down to <see cref="Eip8141Constants.MaxVerifyGas"/>, which is wrong by hundreds of thousands of gas, not
    /// by hundreds.
    /// </remarks>
    private const ulong MaxFrameEntryCharge = 3_000;

    /// <summary>
    /// Environment variable naming the directory that holds the Groth16 sweep artifacts.
    /// </summary>
    /// <remarks>
    /// Required rather than defaulted: the artifacts are generated outside this repository, so any built-in
    /// path is one machine's. Unset, the Groth16 cases skip — which is why the variable must be set
    /// deliberately rather than silently falling back to somewhere that happens to exist.
    /// </remarks>
    private const string Groth16ArtifactRootVariable = "FRAME_GROTH16_ARTIFACTS";

    /// <summary>The revert selector a gnark BN254 verifier raises once the pairing equation evaluates to zero.</summary>
    private static readonly byte[] ProofInvalidSelector = [0x7f, 0xcd, 0xd1, 0xf4];

    /// <summary>
    /// How far the measured frame gas may sit from <c>gas.txt</c>, as a fraction of the expected figure.
    /// </summary>
    /// <remarks>
    /// <c>gas.txt</c> was measured under Foundry, calling a deployed verifier through a <c>staticcall</c>;
    /// a frame runs the same bytecode in a different environment, so this is a cross-check that the payload
    /// is the shipped one, not an equality. The load-bearing proof that the whole proof ran is
    /// <see cref="ProofInvalidSelector"/> plus <see cref="MinPairingCallGas"/>, not this bound.
    /// </remarks>
    private const double Groth16GasTolerance = 0.02;

    /// <summary>
    /// Floor for recognising the <c>ecPairing</c> call in the probe's <c>STATICCALL</c> costs. A 4-pair check
    /// is 45,000 + 4 x 34,000 = 181,000 under EIP-1108; the per-input <c>ecMul</c> and <c>ecAdd</c> calls cost
    /// 6,100 and 250, so any threshold between those bands identifies the pairing unambiguously.
    /// </summary>
    private const long MinPairingCallGas = 150_000;

    private static readonly Address Sender = TestItem.AddressA;
    private static readonly UInt256 SenderBalance = 1_000.Ether;

    /// <summary>
    /// One Groth16 sweep point: the artifact directory, the ceiling its frame declares, and the frame gas
    /// <c>gas.txt</c> measured for the shipped invalid payload.
    /// </summary>
    /// <remarks>
    /// The ceiling is what the frame declares as its execution gas limit, and
    /// <c>TransactionProcessorBase.CapFrameGas</c> bounds the frame at the lesser of that and what remains of
    /// <see cref="Eip8141Constants.MaxVerifyGas"/> — so a declared ceiling under the constant is the whole
    /// budget without touching the constant. 500k is the exception: the 49-input verifier costs 501,141, over
    /// its own nominal label, so its ceiling clears the workload rather than restating the sweep name, and the
    /// case only runs against a build whose constant was raised to match.
    /// </remarks>
    private readonly record struct Groth16Sweep(string Directory, ulong Ceiling, ulong ExpectedFrameGas);

    /// <summary>
    /// What the validation prefix's outermost frame was actually given and actually did, read back out of the
    /// EVM before anything is timed.
    /// </summary>
    /// <param name="Available">Gas the frame entered the EVM with, after <c>CapFrameGas</c> and the target access charge.</param>
    /// <param name="Burned">Gas the frame consumed. The µs/Mgas denominator for every shape that spends what it offers.</param>
    /// <param name="Ops">Instructions the outermost frame executed — the unit the pool's tracing overhead is charged in.</param>
    private readonly record struct FrameGasReadout(ulong Available, ulong Burned, int Ops);

    private static readonly Dictionary<string, Groth16Sweep> Groth16Sweeps = new()
    {
        ["groth16-236k"] = new Groth16Sweep("sweep-236k", 236_285, 234_190),
        ["groth16-300k"] = new Groth16Sweep("sweep-300k", 300_000, 299_256),
        ["groth16-500k"] = new Groth16Sweep("sweep-500k", 510_000, 501_141),
    };

    private ILogManager _logManager = null!;
    private ISpecProvider _specProvider = null!;
    private EthereumEcdsa _ethereumEcdsa = null!;
    private IDbProvider _dbProvider = null!;
    private WorldStateManager _worldStateManager = null!;
    private TestReadOnlyStateProvider _poolState = null!;
    private TestBlockTree _blockTree = null!;
    private TxPool _txPool = null!;
    private FrameTxPrefixSimulator? _realSimulator;
    private List<double> _simulateMicros = null!;

    /// <summary>What the frame under measurement declares as its execution gas limit.</summary>
    private ulong _frameExecutionGasLimit = VerifyGas;

    /// <summary>Signature entries the attacker transaction carries. Empty for every synthetic EVM shape.</summary>
    private TxFrameSignature[] _frameSignatures = [];

    /// <summary>Payload the frame's calldata starts with, before the per-sample salt. Empty for the burn shapes.</summary>
    private byte[] _frameCalldataPrefix = [];

    private IReleaseSpec Spec => _specProvider.GenesisSpec;

    [SetUp]
    public void Setup()
    {
        _logManager = LimboLogs.Instance;
        _specProvider = new TestSpecProvider(Eip8141Prototype.Instance);
        _ethereumEcdsa = new EthereumEcdsa(_specProvider.ChainId);
        _simulateMicros = new List<double>(Warmup + Samples + 1);
        _frameExecutionGasLimit = VerifyGas;
        _frameCalldataPrefix = [];
        _frameSignatures = [];

        // The fixture is one instance for all its tests, so a case that skips before BuildHarness would
        // otherwise leave the previous case's already-disposed objects for TearDown to dispose again.
        _txPool = null!;
        _realSimulator = null;
        _dbProvider = null!;
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_txPool is not null) await _txPool.DisposeAsync();
        _realSimulator?.Dispose();
        _dbProvider?.Dispose();
    }

    /// <summary>
    /// Case 1: a validation prefix that burns its whole per-frame budget and then fails out-of-gas, swept
    /// across the campaign's four ceilings. Three shapes, because gas buys wildly different amounts of CPU:
    /// <c>jump</c> buys interpreter dispatches, <c>keccak</c> buys hashing of one word, <c>keccak-wide</c>
    /// buys hashing of 4 KiB per iteration at six gas a word.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The ceiling is the axis this sweep exists to test. At the EVM layer µs/Mgas is flat across ceilings
    /// within a shape, but admission runs the prefix under <c>FrameTxValidationTracer</c>, whose overhead is
    /// per opcode rather than per gas, so flatness there does not imply flatness here.
    /// </para>
    /// <para>
    /// Every loop holds its opcodes-per-gas ratio constant across ceilings, so the tracer's per-opcode
    /// overhead cannot bend µs/Mgas within a shape. <see cref="Warmup"/> is sized to absorb the tiered JIT's
    /// promotion of the instrumented interpreter, which is otherwise charged to whichever ceiling runs first.
    /// </para>
    /// </remarks>
    [TestCase("jump", Ceiling100k)]
    [TestCase("jump", Ceiling236k)]
    [TestCase("jump", Ceiling300k)]
    [TestCase("jump", Ceiling500k)]
    [TestCase("keccak", Ceiling100k)]
    [TestCase("keccak", Ceiling236k)]
    [TestCase("keccak", Ceiling300k)]
    [TestCase("keccak", Ceiling500k)]
    [TestCase("keccak-wide", Ceiling100k)]
    [TestCase("keccak-wide", Ceiling236k)]
    [TestCase("keccak-wide", Ceiling300k)]
    [TestCase("keccak-wide", Ceiling500k)]
    public void Reject_cost_of_a_budget_burning_prefix(string shape, ulong ceiling) => MeasureFrameRejection(shape, ceiling);

    /// <summary>
    /// Case 2: a prefix that trips the validation tracer on its first instruction. It rejects orders of
    /// magnitude faster than case 1, and having both modes in the campaign is what makes a p99 mean
    /// something rather than restating the p50 of a single-mode distribution.
    /// </summary>
    /// <remarks>
    /// Swept across the same ceilings even though it never gets near one: this shape offers the gas and does
    /// not spend it, so what the sweep moves is the denominator, and its µs/Mgas is the price of the gas an
    /// attacker only has to <em>claim</em>.
    /// </remarks>
    [TestCase(Ceiling100k)]
    [TestCase(Ceiling236k)]
    [TestCase(Ceiling300k)]
    [TestCase(Ceiling500k)]
    public void Reject_cost_of_a_banned_opcode_prefix(ulong ceiling) => MeasureFrameRejection("banned-opcode", ceiling);

    /// <summary>
    /// Case 1b: the motivating workload rather than a synthetic one. A gnark BN254 Groth16 verifier checked
    /// against a wrong public input, so every <c>ecMul</c>, both <c>ecAdd</c>s and all four Miller loops run
    /// and only then does the pairing equation evaluate to zero and the verifier revert <c>ProofInvalid()</c>.
    /// </summary>
    /// <remarks>
    /// The three sweep points differ only in public-input count (8, 18, 49), which is what moves the cost.
    /// Unlike the burn shapes this one does not exhaust its budget — it reverts partway — so the reported
    /// µs/Mgas is against the gas the frame actually consumed, not against the declared ceiling.
    /// </remarks>
    [TestCase("groth16-236k")]
    [TestCase("groth16-300k")]
    [TestCase("groth16-500k")]
    public void Reject_cost_of_a_groth16_verifier_prefix(string shape) =>
        MeasureFrameRejection(shape, Groth16Sweeps[shape].Ceiling);

    /// <summary>
    /// Case 3: the cost of rejecting an ordinary, non-frame transaction — one secp256k1 recovery plus the
    /// cheap state filters — as the denominator for the campaign's "ratio versus a normal rejection".
    /// </summary>
    /// <remarks>
    /// The guard here is the mirror image of the frame cases: the simulator must record <em>no</em> samples,
    /// proving the baseline never enters the EVM.
    /// </remarks>
    [Test]
    public void Reject_cost_of_an_ordinary_transaction()
    {
        BuildHarness(senderCode: []);

        Transaction probe = OrdinaryTx(0);
        AcceptTxResult probeResult = _txPool.SubmitTx(probe, TxHandlingOptions.None);
        Assert.That(probeResult, Is.EqualTo(AcceptTxResult.InsufficientFunds),
            $"the baseline must be rejected for want of funds, not by another filter (got {probeResult})");
        Assert.That(probe.SenderAddress, Is.EqualTo(TestItem.AddressB), "the sender was not recovered, so no ecrecover was paid for");
        Assert.That(_simulateMicros, Is.Empty, "an ordinary transaction must never reach the frame simulator");

        Transaction[] warmup = BuildOrdinarySamples(1, Warmup);
        for (int i = 0; i < warmup.Length; i++) _txPool.SubmitTx(warmup[i], TxHandlingOptions.None);

        Transaction[] samples = BuildOrdinarySamples(1 + Warmup, Samples);
        List<double> submitMicros = new(Samples);
        for (int i = 0; i < samples.Length; i++)
        {
            long start = Stopwatch.GetTimestamp();
            AcceptTxResult result = _txPool.SubmitTx(samples[i], TxHandlingOptions.None);
            submitMicros.Add(Stopwatch.GetElapsedTime(start).TotalMicroseconds);
            if (result != AcceptTxResult.InsufficientFunds)
            {
                Assert.Fail($"sample {i} was not rejected for want of funds: {result}");
            }
        }

        Assert.That(_simulateMicros, Is.Empty, "the baseline must not have run the EVM");

        submitMicros.Sort();
        Emit($"case=ecrecover_baseline shape=ordinary_1559 samples={submitMicros.Count} "
             + $"submit_p50_us={Percentile(submitMicros, 0.50):F1} "
             + $"submit_p99_us={Percentile(submitMicros, 0.99):F1} "
             + $"submit_max_us={submitMicros[^1]:F1} "
             + $"simulate_samples=0 rejected_by=InsufficientFunds");
    }

    private void MeasureFrameRejection(string shape, ulong ceiling)
    {
        bool isGroth16 = Groth16Sweeps.TryGetValue(shape, out Groth16Sweep sweep);
        byte[] code;
        if (isGroth16)
        {
            code = LoadGroth16Sweep(sweep);
        }
        else
        {
            AssertCeilingIsReachable(shape, ceiling);
            _frameExecutionGasLimit = ceiling;
            code = PrefixCode(shape);
        }

        BuildHarness(code);

        // Never assumed, always read back out of the EVM. A ceiling above Eip8141Constants.MaxVerifyGas is
        // silently clamped, and a target whose code is missing runs default verify code instead of the shape;
        // both report the same rejection string the intended work reports.
        FrameGasReadout gas = isGroth16
            ? AssertGroth16FailsAfterThePairing(sweep)
            : ProbeSyntheticShape(shape, ceiling);

        // banned-opcode aborts on its first instruction instead of spending, so its cost is normalised
        // against the gas it offered — which is what the sweep moves. Every other shape is normalised
        // against the gas it actually burned.
        ulong burnedGas = shape == "banned-opcode" ? _frameExecutionGasLimit : gas.Burned;

        long probeFailuresBefore = Volatile.Read(ref Metrics.PendingTransactionsFrameTxSimulationFailed);

        // The guard. Without it a harness whose transaction is dropped by a cheaper upstream filter, or
        // whose payer resolves natively, times an empty admission path and reports healthy-looking numbers
        // for work that never happened.
        Transaction probe = FrameTx(0);
        AcceptTxResult probeResult = _txPool.SubmitTx(probe, TxHandlingOptions.None);
        string probeReason = probeResult.ToString();
        Assert.That(probeResult, Is.EqualTo(AcceptTxResult.FrameSimulationFailed),
            $"the probe must be rejected by the simulation stage, not by a cheaper upstream filter (got {probeReason})");
        Assert.That(_simulateMicros, Is.Not.Empty, "the simulation decorator recorded nothing, so the EVM never ran");
        Assert.That(_txPool.GetPendingTransactionsCount(), Is.Zero, "a rejected frame transaction must not occupy a pool slot");
        Assert.That(Volatile.Read(ref Metrics.PendingTransactionsFrameTxSimulationFailed),
            Is.GreaterThan(probeFailuresBefore), "the simulation-failure counter did not move");
        Assert.That(probeReason, Does.Contain(ExpectedRejectionReason(shape)),
            $"the prefix was rejected, but not the way {shape} rejects, so the sample is not the shape it claims to be");

        Transaction[] warmup = BuildFrameSamples(1, Warmup);
        for (int i = 0; i < warmup.Length; i++) _txPool.SubmitTx(warmup[i], TxHandlingOptions.None);

        Transaction[] samples = BuildFrameSamples(1 + Warmup, Samples);
        List<double> submitMicros = new(Samples);
        _simulateMicros.Clear();

        // Taken after the probe and the warmup, so the emitted count covers the sampled submissions alone.
        long simulationFailuresBefore = Volatile.Read(ref Metrics.PendingTransactionsFrameTxSimulationFailed);

        for (int i = 0; i < samples.Length; i++)
        {
            long start = Stopwatch.GetTimestamp();
            AcceptTxResult result = _txPool.SubmitTx(samples[i], TxHandlingOptions.None);
            submitMicros.Add(Stopwatch.GetElapsedTime(start).TotalMicroseconds);
            if (result != AcceptTxResult.FrameSimulationFailed)
            {
                Assert.Fail($"sample {i} was not rejected by the simulation stage: {result}");
            }
        }

        Assert.That(_simulateMicros, Has.Count.EqualTo(Samples),
            "one simulation per submission is what makes the two spans comparable");

        // Paired, so the difference is a real per-sample residue rather than a difference of two
        // independently ordered distributions.
        List<double> nonEvmMicros = new(Samples);
        for (int i = 0; i < Samples; i++) nonEvmMicros.Add(submitMicros[i] - _simulateMicros[i]);

        List<double> simulateMicros = [.. _simulateMicros];
        submitMicros.Sort();
        simulateMicros.Sort();
        nonEvmMicros.Sort();

        Emit($"case=frame_reject shape={shape} verify_gas={_frameExecutionGasLimit} frame_gas_used={burnedGas} "
             + $"frame_gas_available={gas.Available} frame_gas_burned={gas.Burned} frame_ops={gas.Ops} samples={Samples} "
             + $"submit_p50_us={Percentile(submitMicros, 0.50):F1} "
             + $"submit_p99_us={Percentile(submitMicros, 0.99):F1} "
             + $"submit_max_us={submitMicros[^1]:F1} "
             + $"simulate_p50_us={Percentile(simulateMicros, 0.50):F1} "
             + $"simulate_p99_us={Percentile(simulateMicros, 0.99):F1} "
             + $"simulate_max_us={simulateMicros[^1]:F1} "
             + $"nonevm_p50_us={Percentile(nonEvmMicros, 0.50):F1} "
             + $"nonevm_p99_us={Percentile(nonEvmMicros, 0.99):F1} "
             + $"submit_us_per_Mgas={Percentile(submitMicros, 0.50) * 1_000_000 / burnedGas:F1} "
             + $"simulation_failures={Volatile.Read(ref Metrics.PendingTransactionsFrameTxSimulationFailed) - simulationFailuresBefore} "
             + $"reject_reason=\"{probeReason}\"");
    }

    /// <summary>
    /// Nearest-rank percentile over an already sorted list — no interpolation, no smoothing, so a reported
    /// p99 is an observation that actually occurred.
    /// </summary>
    private static double Percentile(List<double> sorted, double quantile)
    {
        if (sorted.Count == 0) return double.NaN;
        int rank = (int)Math.Ceiling(quantile * sorted.Count);
        return sorted[Math.Clamp(rank, 1, sorted.Count) - 1];
    }

    /// <summary>Cheapest gas per interpreter dispatch: a bare jump loop.</summary>
    private static byte[] JumpLoop() =>
        Prepare.EvmCode
            .Op(Instruction.JUMPDEST)
            .PushData(0)
            .Op(Instruction.JUMP)
            .Done;

    /// <summary>Hashing loop: real work behind each unit of gas.</summary>
    private static byte[] KeccakLoop() =>
        Prepare.EvmCode
            .Op(Instruction.JUMPDEST)
            .PushData(32)
            .PushData(0)
            .Op(Instruction.KECCAK256)
            .Op(Instruction.POP)
            .PushData(0)
            .Op(Instruction.JUMP)
            .Done;

    /// <summary>Memory-expanding hashing loop: the worst realtime-per-gas shape reachable cheaply.</summary>
    private static byte[] KeccakWideLoop() =>
        Prepare.EvmCode
            .Op(Instruction.JUMPDEST)
            .PushData(4096)
            .PushData(0)
            .Op(Instruction.KECCAK256)
            .Op(Instruction.POP)
            .PushData(0)
            .Op(Instruction.JUMP)
            .Done;

    /// <summary>
    /// One banned opcode and stop. <c>TIMESTAMP</c> is legal only inside the canonical expiry verifier, so
    /// <c>FrameTxValidationTracer</c> records the violation on the first instruction and the simulator
    /// rejects on that alone.
    /// </summary>
    private static byte[] BannedOpcode() =>
        Prepare.EvmCode
            .Op(Instruction.TIMESTAMP)
            .Op(Instruction.POP)
            .Op(Instruction.STOP)
            .Done;

    /// <summary>
    /// The ceiling spent on per-signature recovery instead of on frame execution. Rule 6 charges both against
    /// <c>MAX_VERIFY_GAS</c>, so this is the same budget bought differently, and the comparison against the
    /// burn shapes is CPU per unit of declared budget.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Cannot share <see cref="MeasureFrameRejection"/>: this shape is refused at the signature filter, so the
    /// EVM never runs, there is no burned frame gas to divide by, and the assertions there invert. The
    /// denominator is <see cref="FrameTxValidation.ValidationWorkGas"/>, the declared budget the pool charged.
    /// </para>
    /// <para>
    /// secp256k1 only. P256 costs more per signature but is priced 2.4x higher, so it buys less CPU per unit of
    /// budget and is not the worst case.
    /// </para>
    /// <para>
    /// Swept over the same ceilings as the burn shapes. <see cref="Ceiling500k"/> needs no patched build
    /// here, because <see cref="Eip8141Constants.MaxVerifyGas"/> bounds simulation and a signature refusal
    /// never reaches the simulator. It does need a <em>configured</em> 500,000: the declared-gas precheck at
    /// filter stage 7 reads <c>ITxPoolConfig.FrameTxMaxVerifyGas</c>, whose default is 300,000, so a default
    /// node refuses this transaction for free. The pool is therefore configured at the ceiling under test, and
    /// the row describes a node an operator could actually run. Say "no patched build", not "stock node".
    /// </para>
    /// </remarks>
    [TestCase(Ceiling100k)]
    [TestCase(Ceiling236k)]
    [TestCase(Ceiling300k)]
    [TestCase(Ceiling500k)]
    public void Reject_cost_of_a_signature_stuffed_prefix(ulong ceiling)
    {
        int count = StuffedSignatureCount(ceiling);

        // The frame never executes, so its code is irrelevant; only its declared limit enters the budget.
        // The pool is configured at the ceiling under test, so stage 7 enforces the budget this row is
        // labelled with rather than the 300,000 default.
        BuildHarness(PrefixCode("banned-opcode"), ceiling);
        _frameExecutionGasLimit = MinimalFrameGas;
        _frameSignatures = BuildSecp256k1Signatures(count);

        ulong declaredGas = FrameTxValidation.ValidationWorkGas(FrameTx(0));
        Assert.That(declaredGas, Is.LessThanOrEqualTo(ceiling),
            "the declared budget must fit the ceiling it claims, or the row is labelled with a budget the "
            + "transaction never asked for");

        // `count` is only the attacker's optimum if one more entry does not fit. Proving it against the live
        // filter also proves the filter is enforcing this ceiling, which is what makes the row honest.
        long gasRefusalsBefore = Metrics.PendingTransactionsFrameTxVerifyGasTooHigh;
        _frameSignatures = BuildSecp256k1Signatures(count + 1);
        Assert.That(_txPool.SubmitTx(FrameTx(0), TxHandlingOptions.None),
            Is.EqualTo(AcceptTxResult.FrameTxVerifyGasTooHigh),
            $"{count + 1} entries declare more than {ceiling} and must be refused by the declared-gas gate; "
            + "if they are not, the pool is not enforcing the ceiling this row claims");
        Assert.That(Metrics.PendingTransactionsFrameTxVerifyGasTooHigh, Is.EqualTo(gasRefusalsBefore + 1));
        _frameSignatures = BuildSecp256k1Signatures(count);

        long signatureFailuresBefore = Metrics.PendingTransactionsFrameTxSignatureInvalid;
        Assert.That(_txPool.SubmitTx(FrameTx(0), TxHandlingOptions.None),
            Is.Not.EqualTo(AcceptTxResult.Accepted), "the probe was admitted");
        Assert.That(Metrics.PendingTransactionsFrameTxSignatureInvalid, Is.EqualTo(signatureFailuresBefore + 1),
            "the refusal must come from the signature filter, or this measures a cheaper upstream one");
        Assert.That(_simulateMicros, Is.Empty,
            "the EVM must not run: this cost is charged before simulation, which is the point");

        Transaction[] warmup = BuildFrameSamples(1, Warmup);
        for (int i = 0; i < warmup.Length; i++) _txPool.SubmitTx(warmup[i], TxHandlingOptions.None);

        Transaction[] samples = BuildFrameSamples(1 + Warmup, Samples);
        List<double> submitMicros = new(Samples);
        long sampledFailuresBefore = Metrics.PendingTransactionsFrameTxSignatureInvalid;
        for (int i = 0; i < samples.Length; i++)
        {
            long start = Stopwatch.GetTimestamp();
            AcceptTxResult result = _txPool.SubmitTx(samples[i], TxHandlingOptions.None);
            submitMicros.Add(Stopwatch.GetElapsedTime(start).TotalMicroseconds);
            if (result == AcceptTxResult.Accepted) Assert.Fail($"sample {i} was admitted");
        }

        // Without this the row's `samples=` and `evm_ran=no` rest on the single probe above, and any sample
        // short-circuited upstream would be timed as if it had done the curve work.
        Assert.That(Metrics.PendingTransactionsFrameTxSignatureInvalid,
            Is.EqualTo(sampledFailuresBefore + Samples),
            "every timed submission must have been refused by the signature filter");
        Assert.That(_simulateMicros, Is.Empty, "the EVM must not run for any sample");

        submitMicros.Sort();
        double p50 = Percentile(submitMicros, 0.50);
        // Whole-admission p50 over the entry count, so the ~20 other filters' cost is charged to
        // signatures. An independently timed EthereumEcdsa.RecoverAddress puts the residue at a few percent,
        // but the name has to say admission, not recovery.
        double admissionPerSignature = p50 / count;

        // The failure this shape is most exposed to: a memoised verification would collapse the cost and still
        // emit a plausible row. A public-key recovery cannot be this cheap.
        Assert.That(admissionPerSignature, Is.GreaterThan(MinCredibleRecoveryMicros),
            $"{admissionPerSignature:F1} us per signature is too cheap for real curve work; suspect a cache");

        Emit($"case=signature_reject scheme=secp256k1 ceiling={ceiling} signatures={count} "
             + $"declared_gas={declaredGas} samples={Samples} submit_p50_us={p50:F1} "
             + $"submit_p99_us={Percentile(submitMicros, 0.99):F1} admission_us_per_signature={admissionPerSignature:F2} "
             + $"submit_max_us={submitMicros[^1]:F1} tx_bytes={FrameTx(0).GetLength(shouldCountBlobs: false)} "
             + $"submit_us_per_Mgas={p50 / declaredGas * 1_000_000:F1} basis=declared_prefix_gas evm_ran=no");
    }

    /// <summary>
    /// <paramref name="count"/> secp256k1 entries, every one of which the pool fully recovers. The last signs a
    /// different digest, so recovery runs and only then fails the signer compare; a wrong length or a
    /// non-canonical <c>s</c> would be refused before any curve work and measure nothing.
    /// </summary>
    private TxFrameSignature[] BuildSecp256k1Signatures(int count)
    {
        TxFrameSignature[] entries = new TxFrameSignature[count];
        for (int i = 0; i < count; i++)
        {
            byte[] msg = ValueKeccak.Compute(BitConverter.GetBytes(i)).ToByteArray();
            byte[] signed = i == count - 1 ? ValueKeccak.Compute("mismatch"u8).ToByteArray() : msg;
            Signature signature = _ethereumEcdsa.Sign(TestItem.PrivateKeyA, new Hash256(signed));

            byte[] raw = new byte[TxFrameSignature.Secp256k1SignatureLength];
            raw[0] = signature.RecoveryId;
            signature.RAsSpan.CopyTo(raw.AsSpan(1));
            signature.SAsSpan.CopyTo(raw.AsSpan(33));
            entries[i] = new TxFrameSignature(
                TxFrameSignature.SchemeSecp256k1, TestItem.PrivateKeyA.Address, msg, raw);
        }

        return entries;
    }

    private static byte[] PrefixCode(string shape) => shape switch
    {
        "jump" => JumpLoop(),
        "keccak" => KeccakLoop(),
        "keccak-wide" => KeccakWideLoop(),
        "banned-opcode" => BannedOpcode(),
        _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, "unknown prefix shape")
    };

    /// <summary>The rejection each shape must produce, so a run cannot silently measure a different failure.</summary>
    private static string ExpectedRejectionReason(string shape) => shape switch
    {
        "banned-opcode" => "banned opcode TIMESTAMP",
        _ => "validation prefix frame reverted"
    };

    /// <summary>
    /// Skips a sweep point the running binary cannot measure, rather than measuring something else under its
    /// name.
    /// </summary>
    /// <remarks>
    /// <c>CapFrameGas</c> silently bounds a prefix frame at what remains of
    /// <see cref="Eip8141Constants.MaxVerifyGas"/>, and the resulting out-of-gas is indistinguishable at the
    /// pool from the intended one — so a 500k case on an unpatched build would report the constant's numbers
    /// under a 500k label. The constant is <c>const</c>, so no configuration moves it and
    /// <c>ITxPoolConfig.FrameTxMaxVerifyGas</c> gates a different check.
    /// </remarks>
    private static void AssertCeilingIsReachable(string label, ulong ceiling)
    {
        if (ceiling > Eip8141Constants.MaxVerifyGas)
        {
            Assert.Ignore($"{label} needs a frame budget of {ceiling}, but CapFrameGas bounds it at "
                          + $"Eip8141Constants.MaxVerifyGas = {Eip8141Constants.MaxVerifyGas}. The constant is compile-time "
                          + "inlined, so measuring this point takes a source edit and a rebuild.");
        }
    }

    /// <summary>
    /// Proves a synthetic shape got the budget its ceiling claims and did with it what the shape says it does.
    /// </summary>
    /// <returns>The frame's entry gas, the gas it consumed, and how many instructions it ran.</returns>
    /// <remarks>
    /// <para>
    /// Two silent failures are ruled out here, and neither is visible from the pool, which sees the same
    /// <c>"validation prefix frame reverted"</c> string in all three cases. A ceiling over
    /// <see cref="Eip8141Constants.MaxVerifyGas"/> is clamped to the constant and runs out of gas there; a
    /// target missing its code runs EIP-8141 default verify code, which reverts on the empty signature list
    /// without entering the EVM at all.
    /// </para>
    /// <para>
    /// Same construction as <see cref="AssertGroth16FailsAfterThePairing"/>: a direct processing scope built
    /// exactly as the simulator builds its own, with an action-tracing probe the pool's
    /// <c>FrameTxValidationTracer</c> leaves no room for. Not timed, and deliberately outside the sample loop.
    /// </para>
    /// </remarks>
    private FrameGasReadout ProbeSyntheticShape(string shape, ulong ceiling)
    {
        FrameGasReadout readout = ProbeFrame();

        Assert.That(readout.Available, Is.GreaterThanOrEqualTo(ceiling - MaxFrameEntryCharge),
            $"{shape} entered the EVM with {readout.Available} gas against the {ceiling} it declared. CapFrameGas "
            + $"clamped it at Eip8141Constants.MaxVerifyGas = {Eip8141Constants.MaxVerifyGas}, so these are the "
            + "constant's numbers wearing this ceiling's label.");

        if (shape == "banned-opcode")
        {
            // It offers the ceiling and spends none of it; anything else means the sample is a different shape.
            Assert.That(readout.Burned, Is.LessThan(MaxFrameEntryCharge),
                $"{shape} burned {readout.Burned} gas, so it did not abort on its first instruction and the "
                + "sample is not the shape it claims to be");
        }
        else
        {
            Assert.That(readout.Burned, Is.GreaterThanOrEqualTo((ulong)(readout.Available * BudgetBurnFloor)),
                $"{shape} consumed {readout.Burned} of the {readout.Available} gas it was granted, so it did not "
                + "burn its budget and the µs/Mgas denominator is not the work that was done");
        }

        return readout;
    }

    /// <summary>
    /// Runs the frame under measurement once through a direct processing scope, tracing actions and
    /// instructions, and reports what the outermost frame did.
    /// </summary>
    /// <remarks>
    /// Action tracing is the only readout the prefix path offers, since <c>SimulateFrameValidationPrefix</c>
    /// keeps the substate and the frame's gas to itself and returns a bare string. Action tracing is not what
    /// admission runs, which is why this sits outside the timed loop.
    /// </remarks>
    private FrameGasReadout ProbeFrame(FrameGasProbeTracer? tracer = null)
    {
        BlockHeader head = _blockTree.Head!.Header;
        FrameGasProbeTracer probe = tracer ?? new FrameGasProbeTracer();

        using IReadOnlyTxProcessorSource source = new HarnessEnvFactory(_worldStateManager, _specProvider, _logManager).Create();
        using (IReadOnlyTxProcessingScope scope = source.Build(head))
        {
            scope.TransactionProcessor.SetBlockExecutionContext(head);
            scope.TransactionProcessor.Process(FrameTx(0), probe, ExecutionOptions.FrameValidationPrefixOnly);
        }

        Assert.That(probe.TopLevelFrames, Is.EqualTo(1),
            "the sender's code never entered the EVM, so the frame ran EIP-8141 default verify code and the "
            + "measurement would describe a signature-list revert instead of the intended work");

        return new FrameGasReadout(probe.TopLevelFrameGasAvailable, probe.TopLevelFrameGas, probe.TopLevelOps);
    }

    /// <summary>
    /// Installs one Groth16 sweep point: the deployed runtime bytecode becomes the sender's code and the
    /// shipped invalid calldata becomes the frame's payload.
    /// </summary>
    /// <returns>The verifier's runtime bytecode, for the caller to seed as the sender's code.</returns>
    /// <remarks>
    /// Both files are read at run time and neither is re-encoded: <c>calldata-invalid.hex</c> already carries
    /// the 4-byte selector, which differs per sweep point because the signature carries the input count.
    /// </remarks>
    private byte[] LoadGroth16Sweep(Groth16Sweep sweep)
    {
        AssertCeilingIsReachable(sweep.Directory, sweep.Ceiling);

        byte[] verifierCode = Groth16Artifact(sweep, "verifier.hex");
        _frameCalldataPrefix = Groth16Artifact(sweep, "calldata-invalid.hex");
        _frameExecutionGasLimit = sweep.Ceiling;
        return verifierCode;
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

    /// <summary>
    /// Proves the timed samples are the Groth16 workload: the verifier's own code ran, it reverted
    /// <c>ProofInvalid()</c>, and it burned what <c>gas.txt</c> says the pairing costs.
    /// </summary>
    /// <returns>The gas the frame consumed, which is the denominator for this shape's µs/Mgas.</returns>
    /// <remarks>
    /// <para>
    /// The pool cannot tell these apart on its own. A codeless target runs default verify code, which reverts
    /// on the empty signature list without entering the EVM at all and reports the same
    /// <c>"validation prefix frame reverted"</c> string; an exhausted budget under a declared ceiling below
    /// the constant reports that string too. So the shape is pinned here instead, on a direct processing scope
    /// built exactly as the simulator builds its own, with an action-tracing probe the pool's
    /// <c>FrameTxValidationTracer</c> leaves no room for.
    /// </para>
    /// <para>
    /// Not timed, and deliberately outside the sample loop: action tracing is not what admission runs.
    /// </para>
    /// </remarks>
    private FrameGasReadout AssertGroth16FailsAfterThePairing(Groth16Sweep sweep)
    {
        FrameGasProbeTracer probe = new();
        FrameGasReadout readout = ProbeFrame(probe);

        DumpHistogram(sweep.Directory, probe);
        Assert.That(probe.TopLevelRevertOutput, Is.EqualTo(ProofInvalidSelector),
            "the frame did not revert ProofInvalid(), so it failed somewhere other than the pairing equation");
        // The pairing is the expensive half and the whole point of the workload. Assert it ran, from the call
        // costs, rather than inferring it from a total that another environment produced.
        Assert.That(probe.CallCosts, Has.Some.GreaterThan(MinPairingCallGas),
            $"{sweep.Directory} made no ecPairing-sized call, so the prefix failed before the pairing and the "
            + "measurement describes an early exit rather than the full-cost workload");

        long slack = (long)(sweep.ExpectedFrameGas * Groth16GasTolerance);
        Assert.That((long)readout.Burned, Is.EqualTo((long)sweep.ExpectedFrameGas).Within(slack),
            $"{sweep.Directory} burned {readout.Burned} gas against the {sweep.ExpectedFrameGas} its "
            + "gas.txt measured under Foundry. A small offset is expected and documented on Groth16GasTolerance; "
            + "a large one means the payload or the verifier is not the one the artifact ships.");

        return readout;
    }

    private static void DumpHistogram(string label, FrameGasProbeTracer probe)
    {
        List<KeyValuePair<Instruction, (int Count, long Gas)>> rows = [.. probe.Histogram];
        rows.Sort((a, b) => b.Value.Gas.CompareTo(a.Value.Gas));
        foreach (KeyValuePair<Instruction, (int Count, long Gas)> row in rows)
        {
            Diagnostic($"HIST {label} op={row.Key} count={row.Value.Count} gas={row.Value.Gas}");
        }

        Diagnostic($"CALLS {label} " + string.Join(",", probe.CallCosts));
    }

    private Transaction[] BuildFrameSamples(int firstSalt, int count)
    {
        Transaction[] txs = new Transaction[count];
        for (int i = 0; i < count; i++) txs[i] = FrameTx(firstSalt + i);
        return txs;
    }

    private Transaction[] BuildOrdinarySamples(int firstNonce, int count)
    {
        Transaction[] txs = new Transaction[count];
        for (int i = 0; i < count; i++) txs[i] = OrdinaryTx(firstNonce + i);
        return txs;
    }

    /// <summary>
    /// The cheapest transaction that forces a simulation: one <c>self_verify</c> frame with a null target,
    /// no frame signatures, against a sender carrying code.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>FrameTxPayerResolver</c> returns <c>RequiresSimulation</c> unconditionally for that shape once the
    /// sender has code, which is what carries the transaction past the two EVM-free fast paths in
    /// <c>FrameTxSimulationFilter</c>. An empty signature list also short-circuits
    /// <c>FrameTxSignatureFilter</c>, so the attack costs the sender no elliptic-curve work either.
    /// </para>
    /// <para>
    /// The nonce cannot vary: a rejected transaction never enters the pool, so every sample must still be
    /// the sender's next nonce or <c>GapNonceFilter</c> would reject it before the EVM. Distinctness comes
    /// from the frame's calldata instead, which is what an attacker would vary anyway, and without it
    /// <c>AlreadyKnownTxFilter</c> would short-circuit the whole run after the first sample.
    /// </para>
    /// <para>
    /// The salt trails <see cref="_frameCalldataPrefix"/>, so a Groth16 payload keeps its selector and its
    /// arguments byte-for-byte and the verifier ignores the surplus. That the surplus is also free is not
    /// assumed: <see cref="AssertGroth16FailsAfterThePairing"/> checks the burned gas against <c>gas.txt</c>.
    /// </para>
    /// </remarks>
    private Transaction FrameTx(int salt)
    {
        byte[] data = new byte[_frameCalldataPrefix.Length + 32];
        _frameCalldataPrefix.CopyTo(data, 0);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(data.Length - 4), salt);

        Transaction tx = new()
        {
            Type = TxType.FrameTx,
            ChainId = _specProvider.ChainId,
            Nonce = 0,
            SenderAddress = Sender,
            Frames = [new TxFrame(TxFrame.ModeVerify, TxFrame.ApproveExecutionAndPayment, target: null, gasLimit: _frameExecutionGasLimit, UInt256.Zero, data)],
            FrameSignatures = _frameSignatures,
            GasLimit = 1_000_000,
            GasPrice = 1.GWei,
            DecodedMaxFeePerGas = 1.GWei,
        };
        tx.Hash = tx.CalculateHash();
        return tx;
    }

    /// <summary>
    /// An ordinary EIP-1559 transfer from an unfunded account, left unresolved so the pool pays for the
    /// secp256k1 recovery exactly as it does for a gossiped transaction.
    /// </summary>
    private Transaction OrdinaryTx(int nonce) =>
        Build.A.Transaction
            .WithType(TxType.EIP1559)
            .WithChainId(_specProvider.ChainId)
            .WithNonce((ulong)nonce)
            .WithTo(TestItem.AddressC)
            .WithValue(1)
            .WithGasLimit(100_000)
            .WithMaxFeePerGas(1.GWei)
            .WithMaxPriorityFeePerGas(1.GWei)
            .Signed(_ethereumEcdsa, TestItem.PrivateKeyB)
            .TestObject;

    /// <summary>
    /// Wires a real <see cref="TxPool"/> to a real EVM-backed simulator over a state seeded identically in
    /// both stores the admission path reads.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The two stores must agree. The pool sees the chain head through a <see cref="TestReadOnlyStateProvider"/>
    /// that has no EVM and needs only a non-empty code hash for <c>HasCode</c>; the simulator needs a real
    /// <see cref="IWorldState"/> holding the actual bytes. Seeding order matters in the first of them:
    /// <c>CreateAccount</c> replaces the whole account and wipes the code hash, while <c>InsertCode</c>
    /// preserves nonce and balance, so balance goes first and code second.
    /// </para>
    /// <para>
    /// The code in the EVM store is the load-bearing half, and its absence is silent: a codeless target makes
    /// the VERIFY frame run default verify code, which reverts on the empty signature list in microseconds and
    /// reports the same rejection an exhausted budget does. So the seeded head is read back through the same
    /// resettable world state the simulator will build, and the assertion below is what stands between a
    /// measurement and a plausible number for work that never happened.
    /// </para>
    /// </remarks>
    private void BuildHarness(byte[] senderCode, ulong verifyGasCeiling = 0)
    {
        _dbProvider = TestMemDbProvider.Init();
        _worldStateManager = TestWorldStateFactory.CreateWorldStateManagerForTest(_dbProvider, _logManager);

        Hash256 stateRoot;
        IWorldState seedState = new WorldState(_worldStateManager.GlobalWorldState, _logManager);
        using (seedState.BeginScope(IWorldState.PreGenesis))
        {
            seedState.CreateAccount(Sender, SenderBalance);
            if (senderCode.Length > 0) seedState.InsertCode(Sender, senderCode, Spec);
            seedState.Commit(Spec);
            seedState.CommitTree(HeadNumber);
            stateRoot = seedState.StateRoot;
        }

        _poolState = new TestReadOnlyStateProvider();
        _poolState.CreateAccount(Sender, SenderBalance);
        if (senderCode.Length > 0) _poolState.InsertCode(senderCode, Sender);

        _blockTree = new TestBlockTree();
        Block head = Build.A.Block
            .WithNumber(HeadNumber)
            .WithTimestamp(HeadTimestamp)
            .WithBaseFeePerGas(0)
            .WithBeneficiary(TestItem.AddressE)
            .WithGasLimit(BlockGasLimit)
            .WithStateRoot(stateRoot)
            .TestObject;
        _blockTree.Head = head;
        _blockTree.BestSuggestedHeader = head.Header;

        AssertSeededCodeIsVisibleAtHead(head.Header, senderCode);

        _realSimulator = new FrameTxPrefixSimulator(
            new HarnessEnvFactory(_worldStateManager, _specProvider, _logManager),
            _blockTree,
            _specProvider,
            _logManager);

        _txPool = CreatePool(new TimingSimulator(_realSimulator, _simulateMicros), verifyGasCeiling);
    }

    /// <summary>
    /// Reads the seeded head back through a resettable world state built exactly as the simulator builds its
    /// own, so both halves of the two-store seeding are proven before anything is timed.
    /// </summary>
    private void AssertSeededCodeIsVisibleAtHead(BlockHeader head, byte[] senderCode)
    {
        if (senderCode.Length == 0) return;

        Assert.That(_poolState.GetCode(Sender), Is.EqualTo(senderCode),
            "the pool's chain-head view does not carry the sender's code, so the two stores disagree");

        IWorldState headView = new WorldState(_worldStateManager.CreateResettableWorldState(), _logManager);
        using (headView.BeginScope(head))
        {
            Assert.That(headView.GetCode(Sender), Is.EqualTo(senderCode),
                "the simulator's view of the head does not carry the sender's code, so the EVM would run "
                + "default verify code and the measurement would describe the wrong work");
        }
    }

    /// <remarks>
    /// <para>
    /// <paramref name="verifyGasCeiling"/> is what a node operator would configure. <c>0</c> disables the
    /// static declared-gas precheck at filter stage 7 entirely, which is what the burn shapes want: they are
    /// bounded by the <see cref="Eip8141Constants.MaxVerifyGas"/> <c>const</c> in the processor regardless, so
    /// leaving the precheck on would only add a second bound at the same 300,000 and hide which one bit.
    /// </para>
    /// <para>
    /// Shapes refused <em>before</em> the simulator must pass the ceiling instead. For those, <c>0</c> would
    /// measure a transaction no configured node accepts, and the emitted <c>ceiling=</c> field would name a
    /// budget nothing enforced.
    /// </para>
    /// </remarks>
    private TxPool CreatePool(IFrameTxPrefixSimulator frameTxPrefixSimulator, ulong verifyGasCeiling = 0)
    {
        ChainHeadInfoProvider headInfo = new(
            new ChainHeadSpecProvider(_specProvider, _blockTree),
            _blockTree,
            _poolState);

        return new TxPool(
            _ethereumEcdsa,
            new BlobTxStorage(),
            headInfo,
            new TxPoolConfig { GasLimit = BlockGasLimit, FrameTxMaxVerifyGas = verifyGasCeiling },
            new TxValidator(_specProvider.ChainId),
            _logManager,
            new TransactionComparerProvider(_specProvider, _blockTree).GetDefaultComparer(),
            ShouldGossip.Instance,
            incomingTxFilters: null,
            new HeadTxValidator(),
            thereIsPriorityContract: false,
            frameTxPrefixSimulator);
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

    /// <summary>Writes a diagnostic line, kept out of the RESULT stream so that stays parseable as key=value.</summary>
    private static void Diagnostic(string line) => TestContext.Out.WriteLine($"DEBUG {line}");

    private static void Emit(string line)
    {
        string path = Environment.GetEnvironmentVariable("FRAME_MEMPOOL_DOS_OUT")
                      ?? Environment.GetEnvironmentVariable("FRAME_RETRY_OUT")
                      ?? Path.Combine(Path.GetTempPath(), "frame-mempool-dos.txt");
        string record = $"RESULT {line}";
        TestContext.Out.WriteLine(record);
        File.AppendAllText(path, record + Environment.NewLine);
    }

    /// <summary>
    /// Times the EVM stage from the outside, so the inner span is measured without touching production code.
    /// </summary>
    /// <remarks>Shaped after <c>WorldStateMetricsScopeProvider</c>: record in a <c>finally</c>, so a throwing
    /// simulation still contributes a sample instead of silently shortening the distribution.</remarks>
    private sealed class TimingSimulator(IFrameTxPrefixSimulator inner, List<double> samples) : IFrameTxPrefixSimulator
    {
        public FrameTxSimulationResult Simulate(Transaction tx, bool signaturesPreValidated = false, CancellationToken token = default)
        {
            long start = Stopwatch.GetTimestamp();
            try
            {
                return inner.Simulate(tx, signaturesPreValidated, token);
            }
            finally
            {
                samples.Add(Stopwatch.GetElapsedTime(start).TotalMicroseconds);
            }
        }
    }

    /// <summary>
    /// Records what the validation prefix's outermost frame did: how much gas it burned, and how it ended.
    /// </summary>
    /// <remarks>
    /// Action tracing is the only readout the prefix path offers, since <c>SimulateFrameValidationPrefix</c>
    /// keeps the substate and the frame's gas to itself and returns a bare string. Nested frames — every
    /// precompile the verifier calls — report actions too, so entry and exit are paired by depth. A frame that
    /// never enters the EVM at all, which is what EIP-8141 default verify code does, reports nothing, and that
    /// is exactly the silent failure worth catching.
    /// </remarks>
    private sealed class FrameGasProbeTracer : TxTracer
    {
        private int _depth;
        private ulong _entryGas;

        public FrameGasProbeTracer()
        {
            IsTracingActions = true;
            IsTracingInstructions = true;
        }

        public readonly Dictionary<Instruction, (int Count, long Gas)> Histogram = [];
        public readonly List<long> CallCosts = [];
        private Instruction? _pendingOp;
        private ulong _pendingGas;

        public override void StartOperation(int pc, Instruction opcode, ulong gas, in ExecutionEnvironment env)
        {
            if (_depth != 1) return;
            CloseOp(gas);
            _pendingOp = opcode;
            _pendingGas = gas;
        }

        private void CloseOp(ulong gas)
        {
            if (_pendingOp is null) return;
            (int Count, long Gas) prev = Histogram.TryGetValue(_pendingOp.Value, out (int Count, long Gas) v) ? v : (0, 0L);
            long cost = (long)_pendingGas - (long)gas;
            Histogram[_pendingOp.Value] = (prev.Count + 1, prev.Gas + cost);
            if (_pendingOp.Value == Instruction.STATICCALL) CallCosts.Add(cost);
            TopLevelOps++;
            _pendingOp = null;
        }

        /// <summary>Instructions the outermost frame executed, which is what the pool's tracing overhead is charged per.</summary>
        public int TopLevelOps { get; private set; }

        /// <summary>How many outermost frames entered the EVM. Zero means default verify code ran instead.</summary>
        public int TopLevelFrames { get; private set; }

        /// <summary>Gas the outermost frame consumed, excluding the target access its caller pays for.</summary>
        public ulong TopLevelFrameGas { get; private set; }

        /// <summary>
        /// Gas the outermost frame entered the EVM with: what <c>CapFrameGas</c> granted, less the target's
        /// account access. Below the declared ceiling by hundreds means the access charge; by hundreds of
        /// thousands means the ceiling was clamped to <c>Eip8141Constants.MaxVerifyGas</c>.
        /// </summary>
        public ulong TopLevelFrameGasAvailable { get; private set; }

        /// <summary>Revert data of the outermost frame, or empty if it did not revert.</summary>
        public byte[] TopLevelRevertOutput { get; private set; } = [];

        public override void ReportAction(ulong gas, UInt256 value, Address from, Address to, ReadOnlyMemory<byte> input, ExecutionType callType, bool isPrecompileCall = false)
        {
            if (++_depth == 1)
            {
                TopLevelFrames++;
                _entryGas = gas;
                TopLevelFrameGasAvailable = gas;
            }
        }

        public override void ReportActionEnd(ulong gas, ReadOnlyMemory<byte> output) => Leave(gas);

        public override void ReportActionEnd(ulong gas, Address deploymentAddress, ReadOnlyMemory<byte> deployedCode) => Leave(gas);

        public override void ReportActionRevert(ulong gas, ReadOnlyMemory<byte> output)
        {
            if (_depth == 1) TopLevelRevertOutput = output.ToArray();
            Leave(gas);
        }

        /// <remarks>An erroring frame keeps nothing, so the remaining gas it never reports is zero.</remarks>
        public override void ReportActionError(EvmExceptionType evmExceptionType) => Leave(0);

        private void Leave(ulong remainingGas)
        {
            if (_depth == 1) { CloseOp(remainingGas); TopLevelFrameGas = _entryGas - remainingGas; }
            if (_depth > 0) _depth--;
        }
    }

    /// <summary>
    /// The DI-free equivalent of <c>AutoReadOnlyTxProcessingEnvFactory</c>: one resettable world state and a
    /// real transaction processor over it, rebuilt per <c>Build</c> against the head's state root exactly as
    /// production does.
    /// </summary>
    private sealed class HarnessEnvFactory(
        IWorldStateManager worldStateManager,
        ISpecProvider specProvider,
        ILogManager logManager) : IReadOnlyTxProcessingEnvFactory
    {
        public IReadOnlyTxProcessorSource Create()
        {
            IWorldState worldState = new WorldState(worldStateManager.CreateResettableWorldState(), logManager);
            EthereumCodeInfoRepository codeInfoRepository = new(worldState);
            EthereumVirtualMachine virtualMachine = new(new TestBlockhashProvider(specProvider), specProvider, logManager);
            EthereumTransactionProcessor processor = new(
                BlobBaseFeeCalculator.Instance, specProvider, worldState, virtualMachine, codeInfoRepository, logManager);
            return new HarnessEnv(processor, worldState);
        }

        private sealed class HarnessEnv(ITransactionProcessor processor, IWorldState worldState) : IReadOnlyTxProcessorSource
        {
            public IReadOnlyTxProcessingScope Build(BlockHeader? baseBlock) =>
                new ReadOnlyTxProcessingScope(processor, worldState.BeginScope(baseBlock), worldState);

            /// <remarks><see cref="IWorldState"/> is not disposable and the processor holds no unmanaged
            /// resource, so the scope each <see cref="Build"/> opens is the only thing with a lifetime, and
            /// its caller already disposes it.</remarks>
            public void Dispose() { }
        }
    }
}
