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
/// Measures mempool rejection latency for invalid EIP-8141 frame transactions using a real
/// EVM-backed simulator and TxPool. Reports raw-sample p50/p99 for total admission and simulation.
/// </summary>
/// <remarks>
/// Unlike the other measurement harnesses, this exercises the complete admission path with a real
/// simulator. Simulation uses <c>FrameTxValidationTracer</c>, so its timings are not directly comparable
/// with EVM-only measurements using <c>NullTxTracer</c>.
///
/// Results are appended as <c>RESULT key=value</c> lines to <c>FRAME_MEMPOOL_DOS_OUT</c>, then
/// <c>FRAME_RETRY_OUT</c>, or <c>frame-mempool-dos.txt</c> in the temp directory.
/// </remarks>
[TestFixture]
[Explicit("measurement harness")]
[NonParallelizable]
public class FrameTxMempoolDosMeasurement
{
    /// <summary>Untimed submissions used to warm the instrumented interpreter.</summary>
    private const int Warmup = 600;
    private const int Samples = 1_000;
    private const long BlockGasLimit = 30_000_000;
    private const long HeadNumber = 1;
    private const ulong HeadTimestamp = 1_700_000_000;

    /// <summary>Maximum per-frame verification budget enforced by the processor.</summary>
    private const ulong VerifyGas = Eip8141Constants.MaxVerifyGas;

    private const ulong Ceiling100k = 100_000;
    private const ulong Ceiling236k = 236_285;
    private const ulong Ceiling300k = 300_000;
    private const ulong Ceiling500k = 500_000;

    /// <summary>Small frame budget reserved by the signature-stuffing shape.</summary>
    private const ulong MinimalFrameGas = 400;

    /// <summary>Maximum secp256k1 entries that fit after reserving <see cref="MinimalFrameGas"/>.</summary>
    private static int StuffedSignatureCount(ulong ceiling) =>
        (int)((ceiling - MinimalFrameGas) / Eip8141Constants.Secp256k1VerificationGasCost);

    /// <summary>Hardware-relative floor used to detect accidentally cached signature recovery.</summary>
    private const double MinRecoveryFractionForCredibleWork = 0.5;

    private const int RecoveryCalibrationSamples = 200;

    /// <summary>Minimum fraction of available gas a budget-burning shape must consume.</summary>
    private const double BudgetBurnFloor = 0.99;

    /// <summary>Allowed difference between declared frame gas and EVM entry gas.</summary>
    private const ulong MaxFrameEntryCharge = 3_000;

    private const string Groth16ArtifactRootVariable = "FRAME_GROTH16_ARTIFACTS";

    private static readonly byte[] ProofInvalidSelector = [0x7f, 0xcd, 0xd1, 0xf4];

    /// <summary>Tolerance when cross-checking Groth16 frame gas against <c>gas.txt</c>.</summary>
    private const double Groth16GasTolerance = 0.02;

    /// <summary>Threshold distinguishing the Groth16 pairing call from ecMul/ecAdd calls.</summary>
    private const long MinPairingCallGas = 150_000;

    private const long Bn254FourPairPrice = 181_000;

    private const long PairingPriceTolerance = 3_000;

    private static readonly Address Sender = TestItem.AddressA;
    private static readonly UInt256 SenderBalance = 1_000.Ether;

    private readonly record struct Groth16Sweep(
        string Directory, ulong Ceiling, ulong ExpectedFrameGas, Groth16Failure Failure);

    private enum Groth16Failure
    {
        RevertsProofInvalid,
        ReturnsFalse,
    }

    private readonly record struct FrameGasReadout(ulong Available, ulong Burned, int Ops);

    private static readonly Dictionary<string, Groth16Sweep> Groth16Sweeps = new()
    {
        ["groth16-236k"] = new Groth16Sweep("sweep-236k", 236_285, 234_190, Groth16Failure.RevertsProofInvalid),
        ["groth16-300k"] = new Groth16Sweep("sweep-300k", 300_000, 299_256, Groth16Failure.RevertsProofInvalid),
        // The result key records the actual ceiling; sweep-500k is the generator's artifact name.
        ["groth16-510k"] = new Groth16Sweep("sweep-500k", 510_000, 501_141, Groth16Failure.RevertsProofInvalid),
        ["groth16-soispoke"] = new Groth16Sweep("sweep-soispoke", 300_000, 248_437, Groth16Failure.ReturnsFalse),
    };

    private static IEnumerable<TestCaseData> BudgetBurningCases()
    {
        foreach (string shape in new string[] { "jump", "keccak" })
        {
            foreach (ulong ceiling in new ulong[] { Ceiling100k, Ceiling300k, Ceiling500k })
            {
                yield return new TestCaseData(shape, ceiling);
            }
        }

        foreach (ulong ceiling in new ulong[] { Ceiling100k, Ceiling236k, Ceiling300k, Ceiling500k })
        {
            yield return new TestCaseData("keccak-wide", ceiling);
        }
    }

    private static IEnumerable<TestCaseData> Groth16Cases()
    {
        foreach (string shape in new string[] { "groth16-236k", "groth16-300k", "groth16-510k", "groth16-soispoke" })
        {
            yield return new TestCaseData(shape);
        }
    }

    private static IEnumerable<TestCaseData> CeilingCases()
    {
        foreach (ulong ceiling in new ulong[] { Ceiling100k, Ceiling236k, Ceiling300k, Ceiling500k })
        {
            yield return new TestCaseData(ceiling);
        }
    }

    private long _lastPairingCallGas;

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

    private ulong _frameExecutionGasLimit = VerifyGas;

    private TxFrameSignature[] _frameSignatures = [];

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
    /// Measures rejection of prefixes that exhaust their frame budget. Shapes vary CPU work per unit
    /// of gas while keeping their behavior stable across ceilings.
    /// </summary>
    [TestCaseSource(nameof(BudgetBurningCases))]
    public void Reject_cost_of_a_budget_burning_prefix(string shape, ulong ceiling) => MeasureFrameRejection(shape, ceiling);

    /// <summary>Measures immediate rejection caused by a banned validation-prefix opcode.</summary>
    [TestCase(Ceiling100k)]
    public void Reject_cost_of_a_banned_opcode_prefix(ulong ceiling) => MeasureFrameRejection("banned-opcode", ceiling);

    /// <summary>Measures rejection after a complete Groth16 verification with an invalid proof or input.</summary>
    [TestCaseSource(nameof(Groth16Cases))]
    public void Reject_cost_of_a_groth16_verifier_prefix(string shape) =>
        MeasureFrameRejection(shape, Groth16Sweeps[shape].Ceiling);

    /// <summary>Measures ordinary transaction rejection as the non-frame admission baseline.</summary>
    [Test]
    public void Reject_cost_of_an_ordinary_transaction()
    {
        BuildHarness(senderCode: []);

        Transaction probe = OrdinaryTx(0);
        AcceptTxResult probeResult = _txPool.SubmitTx(probe, TxHandlingOptions.None);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(probeResult, Is.EqualTo(AcceptTxResult.InsufficientFunds),
                $"the baseline must be rejected for want of funds, not by another filter (got {probeResult})");
            Assert.That(probe.SenderAddress, Is.EqualTo(TestItem.AddressB), "the sender was not recovered, so no ecrecover was paid for");
            Assert.That(_simulateMicros, Is.Empty, "an ordinary transaction must never reach the frame simulator");
        }

        SubmitWarmup(BuildOrdinarySamples(1, Warmup));

        List<double> submitMicros = TimeSamples(BuildOrdinarySamples(1 + Warmup, Samples),
            result => result == AcceptTxResult.InsufficientFunds,
            (i, result) => $"sample {i} was not rejected for want of funds: {result}");

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

        // Probe outside the timed loop to verify that the intended workload entered the EVM.
        FrameGasReadout gas = isGroth16
            ? AssertGroth16FailsAfterThePairing(sweep)
            : ProbeSyntheticShape(shape, ceiling);

        // Immediate rejection spends almost no gas, so normalize it by offered rather than burned gas.
        ulong burnedGas = shape == "banned-opcode" ? _frameExecutionGasLimit : gas.Burned;

        long probeFailuresBefore = Volatile.Read(ref Metrics.PendingTransactionsFrameTxSimulationFailed);

        Transaction probe = FrameTx(0);
        AcceptTxResult probeResult = _txPool.SubmitTx(probe, TxHandlingOptions.None);
        string probeReason = probeResult.ToString();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(probeResult, Is.EqualTo(AcceptTxResult.FrameSimulationFailed),
                $"the probe must be rejected by the simulation stage, not by a cheaper upstream filter (got {probeReason})");
            Assert.That(_simulateMicros, Is.Not.Empty, "the simulation decorator recorded nothing, so the EVM never ran");
            Assert.That(_txPool.GetPendingTransactionsCount(), Is.Zero, "a rejected frame transaction must not occupy a pool slot");
            Assert.That(Volatile.Read(ref Metrics.PendingTransactionsFrameTxSimulationFailed),
                Is.GreaterThan(probeFailuresBefore), "the simulation-failure counter did not move");
            Assert.That(probeReason, Does.Contain(ExpectedRejectionReason(shape)),
                $"the prefix was rejected, but not the way {shape} rejects, so the sample is not the shape it claims to be");
        }

        SubmitWarmup(BuildFrameSamples(1, Warmup));

        Transaction[] samples = BuildFrameSamples(1 + Warmup, Samples);
        _simulateMicros.Clear();

        long simulationFailuresBefore = Volatile.Read(ref Metrics.PendingTransactionsFrameTxSimulationFailed);

        List<double> submitMicros = TimeSamples(samples,
            result => result == AcceptTxResult.FrameSimulationFailed,
            (i, result) => $"sample {i} was not rejected by the simulation stage: {result}");

        Assert.That(_simulateMicros, Has.Count.EqualTo(Samples),
            "one simulation per submission is what makes the two spans comparable");

        // Samples are paired, so subtraction gives the non-EVM cost for each submission.
        List<double> nonEvmMicros = new(Samples);
        for (int i = 0; i < Samples; i++) nonEvmMicros.Add(submitMicros[i] - _simulateMicros[i]);

        List<double> simulateMicros = [.. _simulateMicros];
        submitMicros.Sort();
        simulateMicros.Sort();
        nonEvmMicros.Sort();

        Emit($"case=frame_reject shape={shape} verify_gas={_frameExecutionGasLimit} frame_gas_used={burnedGas} "
             + (isGroth16
                 ? $"pairing_call_gas={_lastPairingCallGas} fits_500k={(sweep.ExpectedFrameGas <= Ceiling500k ? "yes" : "no")} "
                 : "")
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

    // Nearest-rank keeps every reported percentile tied to an observed sample.
    private static double Percentile(List<double> sorted, double quantile)
    {
        if (sorted.Count == 0) return double.NaN;
        int rank = (int)Math.Ceiling(quantile * sorted.Count);
        return sorted[Math.Clamp(rank, 1, sorted.Count) - 1];
    }

    private void SubmitWarmup(Transaction[] warmup)
    {
        for (int i = 0; i < warmup.Length; i++) _txPool.SubmitTx(warmup[i], TxHandlingOptions.None);
    }

    private List<double> TimeSamples(
        Transaction[] samples, Func<AcceptTxResult, bool> isExpected, Func<int, AcceptTxResult, string> describeFailure)
    {
        List<double> submitMicros = new(samples.Length);
        for (int i = 0; i < samples.Length; i++)
        {
            long start = Stopwatch.GetTimestamp();
            AcceptTxResult result = _txPool.SubmitTx(samples[i], TxHandlingOptions.None);
            submitMicros.Add(Stopwatch.GetElapsedTime(start).TotalMicroseconds);
            if (!isExpected(result)) Assert.Fail(describeFailure(i, result));
        }
        return submitMicros;
    }

    private static byte[] JumpLoop() =>
        Prepare.EvmCode
            .Op(Instruction.JUMPDEST)
            .PushData(0)
            .Op(Instruction.JUMP)
            .Done;

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

    // TIMESTAMP is rejected immediately outside the canonical expiry verifier.
    private static byte[] BannedOpcode() =>
        Prepare.EvmCode
            .Op(Instruction.TIMESTAMP)
            .Op(Instruction.POP)
            .Op(Instruction.STOP)
            .Done;

    [TestCaseSource(nameof(CeilingCases))]
    public void Reject_cost_of_a_signature_stuffed_prefix(ulong ceiling)
    {
        int count = StuffedSignatureCount(ceiling);

        BuildHarness(PrefixCode("banned-opcode"), ceiling);
        _frameExecutionGasLimit = MinimalFrameGas;
        _frameSignatures = BuildSecp256k1Signatures(count);

        ulong declaredGas = FrameTxValidation.ValidationWorkGas(FrameTx(0));
        Assert.That(declaredGas, Is.LessThanOrEqualTo(ceiling),
            "the declared budget must fit the ceiling it claims, or the row is labelled with a budget the "
            + "transaction never asked for");

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

        SubmitWarmup(BuildFrameSamples(1, Warmup));

        Transaction[] samples = BuildFrameSamples(1 + Warmup, Samples);
        long sampledFailuresBefore = Metrics.PendingTransactionsFrameTxSignatureInvalid;
        List<double> submitMicros = TimeSamples(samples,
            result => result != AcceptTxResult.Accepted,
            (i, _) => $"sample {i} was admitted");

        Assert.That(Metrics.PendingTransactionsFrameTxSignatureInvalid,
            Is.EqualTo(sampledFailuresBefore + Samples),
            "every timed submission must have been refused by the signature filter");
        Assert.That(_simulateMicros, Is.Empty, "the EVM must not run for any sample");

        submitMicros.Sort();
        double p50 = Percentile(submitMicros, 0.50);
        double admissionPerSignature = p50 / count;

        double recoveryMicros = TimeOneRecovery();
        Assert.That(admissionPerSignature, Is.GreaterThan(recoveryMicros * MinRecoveryFractionForCredibleWork),
            $"{admissionPerSignature:F1} us per signature against a locally timed recovery of "
            + $"{recoveryMicros:F1} us is too cheap for real curve work; suspect a cache");

        Emit($"case=signature_reject scheme=secp256k1 ceiling={ceiling} signatures={count} "
             + $"declared_gas={declaredGas} samples={Samples} submit_p50_us={p50:F1} "
             + $"submit_p99_us={Percentile(submitMicros, 0.99):F1} admission_us_per_signature={admissionPerSignature:F2} "
             + $"submit_max_us={submitMicros[^1]:F1} tx_bytes={FrameTx(0).GetLength(shouldCountBlobs: false)} "
             + $"submit_us_per_Mgas={p50 / declaredGas * 1_000_000:F1} basis=declared_prefix_gas evm_ran=no");
    }

    /// <summary>Times median secp256k1 recovery cost using the admission implementation.</summary>
    private double TimeOneRecovery()
    {
        Hash256 digest = new(ValueKeccak.Compute("calibration"u8).ToByteArray());
        Signature signature = _ethereumEcdsa.Sign(TestItem.PrivateKeyA, digest);

        for (int i = 0; i < RecoveryCalibrationSamples; i++) _ethereumEcdsa.RecoverAddress(signature, digest);

        List<double> micros = new(RecoveryCalibrationSamples);
        for (int i = 0; i < RecoveryCalibrationSamples; i++)
        {
            long start = Stopwatch.GetTimestamp();
            _ethereumEcdsa.RecoverAddress(signature, digest);
            micros.Add(Stopwatch.GetElapsedTime(start).TotalMicroseconds);
        }

        micros.Sort();
        return Percentile(micros, 0.50);
    }

    /// <summary>
    /// Builds secp256k1 entries that all require recovery, with the final entry failing signer validation.
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

    private static string ExpectedRejectionReason(string shape) => shape switch
    {
        "banned-opcode" => "banned opcode TIMESTAMP",
        _ when Groth16Sweeps.TryGetValue(shape, out Groth16Sweep sweep)
               && sweep.Failure == Groth16Failure.ReturnsFalse
            => "validation prefix never set a payer",
        _ => "validation prefix frame reverted"
    };

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
    /// Probes a synthetic shape outside the timed path and verifies that it received and consumed the gas
    /// its measurement claims.
    /// </summary>
    private FrameGasReadout ProbeSyntheticShape(string shape, ulong ceiling)
    {
        FrameGasReadout readout = ProbeFrame();

        Assert.That(readout.Available, Is.GreaterThanOrEqualTo(ceiling - MaxFrameEntryCharge),
            $"{shape} entered the EVM with {readout.Available} gas against the {ceiling} it declared. CapFrameGas "
            + $"clamped it at Eip8141Constants.MaxVerifyGas = {Eip8141Constants.MaxVerifyGas}, so these are the "
            + "constant's numbers wearing this ceiling's label.");

        if (shape == "banned-opcode")
        {
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
    /// Executes one validation-prefix transaction with action and instruction tracing, returning top-level
    /// frame gas and instruction counts.
    /// </summary>
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

    /// <summary>Loads runtime bytecode and invalid calldata for one Groth16 sweep point.</summary>
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
    /// Verifies that the Groth16 workload reached a correctly priced four-pair BN254 pairing and consumed
    /// approximately the expected gas.
    /// </summary>
    private FrameGasReadout AssertGroth16FailsAfterThePairing(Groth16Sweep sweep)
    {
        FrameGasProbeTracer probe = new();
        FrameGasReadout readout = ProbeFrame(probe);

        DumpHistogram(sweep.Directory, probe);
        Assert.That(readout.Available, Is.GreaterThanOrEqualTo(sweep.Ceiling - MaxFrameEntryCharge),
            $"{sweep.Directory} entered the EVM with {readout.Available} gas against the {sweep.Ceiling} it "
            + $"declared. CapFrameGas clamped it at Eip8141Constants.MaxVerifyGas = {Eip8141Constants.MaxVerifyGas}, "
            + "so these are the constant's numbers wearing this ceiling's label.");
        if (sweep.Failure == Groth16Failure.RevertsProofInvalid)
        {
            Assert.That(probe.TopLevelRevertOutput, Is.EqualTo(ProofInvalidSelector),
                "the frame did not revert ProofInvalid(), so it failed somewhere other than the pairing equation");
        }
        else
        {
            Assert.That(probe.TopLevelRevertOutput, Is.Null.Or.Empty,
                "this verifier returns false rather than reverting, so a revert means the frame failed for "
                + "some reason other than the pairing equation");
        }
        Assert.That(probe.CallCosts, Has.Some.GreaterThan(MinPairingCallGas),
            $"{sweep.Directory} made no ecPairing-sized call, so the prefix failed before the pairing and the "
            + "measurement describes an early exit rather than the full-cost workload");
        _lastPairingCallGas = 0;
        foreach (long cost in probe.CallCosts)
        {
            if (cost > _lastPairingCallGas) _lastPairingCallGas = cost;
        }

        Assert.That(_lastPairingCallGas, Is.EqualTo(Bn254FourPairPrice).Within(PairingPriceTolerance),
            $"{sweep.Directory}'s pairing call cost {_lastPairingCallGas} against a priced "
            + $"{Bn254FourPairPrice}. Above the band means ecPairing errored and burned the gas forwarded to "
            + "it, which charges full price for no curve work; below means it is not a 4-pair check.");

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
    /// Builds a frame transaction with a fixed nonce and varying calldata salt, keeping rejected samples at
    /// the next nonce while giving each a distinct hash.
    /// </summary>
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
    /// Creates a TxPool and real frame simulator backed by equivalent pool-state and EVM-state views.
    /// </summary>
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

        // The per-head budget sheds admission after a second of simulation against one head. This harness
        // times single rejections against a fixed head, so leaving it at the default would measure the
        // shed path instead of the prefix; the flood harness is where shedding belongs.
        TxPoolConfig txPoolConfig = new()
        {
            GasLimit = BlockGasLimit,
            FrameTxMaxVerifyGas = verifyGasCeiling,
            FrameTxSimulationBudgetPerHeadMs = int.MaxValue,
        };
        _realSimulator = new FrameTxPrefixSimulator(
            new HarnessEnvFactory(_worldStateManager, _specProvider, _logManager),
            _blockTree,
            _specProvider,
            txPoolConfig,
            _logManager);

        _txPool = CreatePool(new TimingSimulator(_realSimulator, _simulateMicros), txPoolConfig);
    }

    /// <summary>Confirms that both pool and EVM views contain the sender code before measurement.</summary>
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

    private TxPool CreatePool(IFrameTxPrefixSimulator frameTxPrefixSimulator, TxPoolConfig txPoolConfig)
    {
        ChainHeadInfoProvider headInfo = new(
            new ChainHeadSpecProvider(_specProvider, _blockTree),
            _blockTree,
            _poolState);

        return new TxPool(
            _ethereumEcdsa,
            new BlobTxStorage(),
            headInfo,
            txPoolConfig,
            new TxValidator(_specProvider.ChainId),
            new SpecChangeTxValidator(_specProvider.ChainId),
            _logManager,
            new TransactionComparerProvider(_specProvider, _blockTree).GetDefaultComparer(),
            ShouldGossip.Instance,
            incomingTxFilters: null,
            thereIsPriorityContract: false,
            frameTxPrefixSimulator);
    }

    /// <summary>Returns the externally generated artifact root; no machine-specific fallback is valid.</summary>
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

    /// <summary>Times the real simulator without modifying production code.</summary>
    private sealed class TimingSimulator(IFrameTxPrefixSimulator inner, List<double> samples) : IFrameTxPrefixSimulator
    {
        public FrameTxSimulationResult Simulate(
            Transaction tx,
            bool signaturesPreValidated = false,
            CancellationToken token = default,
            bool local = false)
        {
            long start = Stopwatch.GetTimestamp();
            try
            {
                return inner.Simulate(tx, signaturesPreValidated, token, local);
            }
            finally
            {
                samples.Add(Stopwatch.GetElapsedTime(start).TotalMicroseconds);
            }
        }
    }

    /// <summary>Traces the outermost validation frame to record gas, instructions, calls, and termination.</summary>
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

        public int TopLevelOps { get; private set; }

        public int TopLevelFrames { get; private set; }

        public ulong TopLevelFrameGas { get; private set; }

        public ulong TopLevelFrameGasAvailable { get; private set; }

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

        public override void ReportActionError(EvmExceptionType evmExceptionType) => Leave(0);

        private void Leave(ulong remainingGas)
        {
            if (_depth == 1) { CloseOp(remainingGas); TopLevelFrameGas = _entryGas - remainingGas; }
            if (_depth > 0) _depth--;
        }
    }

    /// <summary>Minimal read-only environment matching the production transaction-processing setup.</summary>
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

            public void Dispose() { }
        }
    }
}
