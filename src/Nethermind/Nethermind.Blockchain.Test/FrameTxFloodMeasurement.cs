// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
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

namespace Nethermind.Blockchain.Test;

/// <summary>
/// Measures what invalid EIP-8141 frame transactions cost a node while they contend with block building, block import
/// and honest traffic on one CPU. The suites live in <c>FrameTxFloodMeasurement.Suites.cs</c>; this file holds the
/// chain, the open-loop flood generator and the guard that each attack shape reaches its intended rejection stage.
/// </summary>
/// <remarks>
/// Achieved rate and scheduling lag are reported so saturation cannot look like a cheap flood. Run under
/// <c>taskset -c 0</c>; developer-machine timings are indicative only. Each suite is a test category, so a CI run can
/// dispatch one question alone.
/// </remarks>
[TestFixture]
[Explicit("measurement harness")]
[NonParallelizable]
public partial class FrameTxFloodMeasurement
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

    private bool _shedding;

    /// <summary>Whether import victims raise the block tree's processing flag, as the node's processing loop does.</summary>
    /// <remarks>Gossip admission yields to block processing on this flag: a simulation neither starts nor keeps running
    /// while it is set. Only the periodic arm raises it: a victim processing back to back would hold the flag almost
    /// continuously and defer every flood transaction, so those arms measure import without the signal. The producer
    /// never raises it, as block building does not. Static so every row, emitted from any helper, can report it; the
    /// fixture is non-parallelizable.</remarks>
    private static bool s_blockProcessingSignal;

    private byte[] _frameCalldataPrefix = [];

    private TxFrameSignature[] _frameSignatures = [];

    private ulong _frameExecutionGasLimit;

    private FloodTestBlockchain _chain = null!;
    private BlockHeader _parent = null!;
    private Block _workloadBlock = null!;
    private Transaction[] _floodTxs = null!;

    /// <summary>Tracks fresh calldata salts so rejected hashes never bypass simulation through the known cache.</summary>
    private int _saltCursor;

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

    private const double RateHeldFloor = 0.95;


    private readonly record struct FloodOutcome(
        double OfferedRate,
        double AchievedRate,
        int Submitted,
        int Rejected,
        double MaxLagUs,
        int PendingPoolGrowth,
        int Shed,
        List<double> ProcessMicros,
        GcDelta Gc = default);

    /// <summary>Single source of truth for shed percentage, so every suite's rows agree.</summary>
    private static double ShedPct(FloodOutcome outcome) =>
        outcome.Submitted > 0 ? 100.0 * outcome.Shed / outcome.Submitted : 0;

    [SetUp]
    public void Setup()
    {
        s_blockProcessingSignal = false;
        _chain = null!;
        _frameCalldataPrefix = [];
        _frameSignatures = [];
    }

    [TearDown]
    public void TearDown() => _chain?.Dispose();

    /// <summary>Verifies that each flood shape reaches its intended rejection stage before timing.</summary>
    [TestCaseSource(nameof(AdmissionShapes))]
    [Category(CostSuite)]
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

                    if (submit(AsDecoded(txs[i % txs.Length])) == AcceptTxResult.FrameSimulationFailed) Interlocked.Increment(ref Rejected);
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

        /// <summary>A copy made at submission, as a node holds a gossiped transaction it has just decoded.</summary>
        /// <remarks>Pool entries are built ahead and age into the oldest generation, and a signature entry caches its
        /// recovered signer. Submitting them directly would attach that young cache to old objects and promote it,
        /// filling the oldest generation with work a node never does. A copy with its own entries dies young, and
        /// pays every recovery itself.</remarks>
        private static Transaction AsDecoded(Transaction pooled)
        {
            Transaction decoded = new();
            pooled.CopyTo(decoded, copyHash: true);
            if (pooled.FrameSignatures is { Length: > 0 } signatures) decoded.FrameSignatures = FrameTxTestFrames.Fresh(signatures);
            return decoded;
        }

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

    private static Func<long>? RejectionCounterFor(string shape) =>
        shape == "signature-stuffed"
            ? () => Nethermind.TxPool.Metrics.PendingTransactionsFrameTxSignatureInvalid
            : null;

    private void RunFor(TimeSpan window)
    {
        if (window <= TimeSpan.Zero) return;
        long end = Stopwatch.GetTimestamp() + (long)(window.TotalSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < end) ProcessOnce();
    }

    private void ProcessOnce() => ProcessImport(_workloadBlock);

    /// <summary>Processes <paramref name="block"/> on the main branch processor the way the import loop does: with the
    /// block tree's processing flag raised, unless the arm measures a node without that signal.</summary>
    private void ProcessImport(Block block)
    {
        bool signal = s_blockProcessingSignal;
        if (signal) _chain.BlockTree.IsProcessingBlock = true;
        try
        {
            _chain.BranchProcessor.Process(_parent, [block], ProcessingOptions.NoValidation, NullBlockTracer.Instance);
        }
        finally
        {
            if (signal) _chain.BlockTree.IsProcessingBlock = false;
        }
    }

    /// <summary>
    /// Runs an open-loop flood using absolute deadlines, preserving offered load when simulation falls behind.
    /// </summary>
    /// <remarks>
    /// The optional arguments serve the declared gas/s arms: <paramref name="submit"/> routes the flood through the
    /// gossip scheduler, <paramref name="window"/> and
    /// <paramref name="poolSize"/> size longer or faster windows, and <paramref name="onWindowEnd"/> reads counters
    /// that only exist after the generator stops. Left unset, the flood is the one every earlier arm measures.
    /// </remarks>
    private FloodOutcome MeasureUnderFloodGeneric(
        int offeredRate, Action warmup, Func<TimeSpan, List<double>> measure, Func<long>? rejectionCounter = null,
        Action? onWindowStart = null, Func<Transaction, AcceptTxResult>? submit = null, TimeSpan? window = null, int poolSize = FloodPoolSize, Action? onWindowEnd = null)
    {
        _floodTxs = BuildFloodTransactions(_saltCursor, poolSize);
        _saltCursor += poolSize;

        using FloodGenerator generator = new(
            submit ?? (tx => _chain.TxPool.SubmitTx(tx, TxHandlingOptions.None)), _floodTxs, offeredRate);

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
            GcDelta.Snapshot gcAtStart = GcDelta.Take();

            List<double> sampleMicros = measure(window ?? MeasureWindow);

            GcDelta gcInWindow = GcDelta.Since(gcAtStart);
            long windowEnd = Stopwatch.GetTimestamp();

            // Every counter is read after the join. Reading them while the generator still submits lets a
            // transaction land between two reads and be counted by one but not the other, which breaks the
            // accounting the rows assert on.
            Assert.That(generator.Stop(), Is.True,
                "the generator did not stop, so its counters would be read while it still writes them");
            onWindowEnd?.Invoke();

            int submittedInWindow = generator.Submitted - submittedAtStart;
            int rejectedInWindow = rejectionCounter is null
                ? generator.Rejected - rejectedAtStart
                : (int)(rejectionCounter() - rejectionCounterAtStart);
            int pendingPoolGrowth = _chain.TxPool.GetPendingTransactionsCount() - pendingAtStart;
            int shedInWindow = (int)(ShedCount() - shedAtStart);

            double windowSeconds = (windowEnd - windowStart) / (double)Stopwatch.Frequency;
            double achieved = windowSeconds > 0 ? submittedInWindow / windowSeconds : 0;

            return new FloodOutcome(offeredRate, achieved, submittedInWindow, rejectedInWindow, generator.MaxLagUs,
                pendingPoolGrowth, shedInWindow, sampleMicros, gcInWindow);
        });
    }

    /// <summary>
    /// Admission the simulator refused or deferred without finishing the prefix: the per-head budget is spent, the
    /// simulator is already busy, or block processing preempted it. None of these is a rejection.
    /// </summary>
    private static long ShedCount() =>
        Volatile.Read(ref Nethermind.TxPool.Metrics.FrameTxSimulationsBudgetExhausted)
        + Volatile.Read(ref Nethermind.TxPool.Metrics.FrameTxSimulationsBusy)
        + Volatile.Read(ref Nethermind.TxPool.Metrics.FrameTxSimulationsPreempted);

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
    private async Task BuildChain(string shape, ulong ceiling, bool shedding = false, bool computeVictim = false, int honestSenders = 0)
    {
        byte[] attackCode = LoadAttackCode(shape, ceiling);
        byte[] computeVictimCode = FrameTxPrefixShapes.Code("keccak-wide");
        byte[] honestCode = HonestVerifyCode();

        _shedding = shedding;
        ulong verifyGasCeiling = shape == "signature-stuffed" ? ceiling : 0;
        _chain = await FloodTestBlockchain.CreateFlood(verifyGasCeiling, shedding, computeVictim, builder =>
        {
            builder.AddSingleton<ISpecProvider>(new TestSpecProvider(Eip8141Prototype.Instance));
            builder.AddScoped<IGenesisPostProcessor, IWorldState, ISpecProvider>((worldState, specProvider) =>
                new FunctionalGenesisPostProcessor(_ =>
                {
                    worldState.CreateAccount(Attacker, AttackerBalance);
                    worldState.InsertCode(Attacker, attackCode, specProvider.GenesisSpec);
                    if (computeVictim)
                    {
                        worldState.CreateAccount(ComputeVictimContract, UInt256.Zero);
                        worldState.InsertCode(ComputeVictimContract, computeVictimCode, specProvider.GenesisSpec);
                    }
                    for (int i = 0; i < honestSenders; i++)
                    {
                        worldState.CreateAccount(HonestSender(i), AttackerBalance);
                        worldState.InsertCode(HonestSender(i), honestCode, specProvider.GenesisSpec);
                    }
                    worldState.RecalculateStateRoot();
                }));
        });

        _parent = _chain.BlockTree.Head!.Header;
        _workloadBlock = BuildWorkloadBlock();
        if (computeVictim) _computeBlock = BuildComputeBlock();
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

    private Transaction[] BuildFloodTransactions(int saltBase, int count = FloodPoolSize)
    {
        Transaction[] txs = new Transaction[count];
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
            // Shared here; the generator gives each submission its own entries as it submits.
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
        private bool _computeVictim;

        public static async Task<FloodTestBlockchain> CreateFlood(
            ulong verifyGasCeiling, bool shedding, bool computeVictim, Action<ContainerBuilder>? configurer = null)
        {
            FloodTestBlockchain chain = new() { _verifyGasCeiling = verifyGasCeiling, _shedding = shedding, _computeVictim = computeVictim };
            await chain.Build(configurer);
            return chain;
        }

        protected override IEnumerable<IConfig> CreateConfigs() =>
        [
            // A node pre-warms and parallelises on spare cores. Pinned to one core, both only re-run the compute
            // victim's calls on the core under measurement, so that victim runs its block once, serially.
            _computeVictim
                ? new BlocksConfig { MinGasPrice = 0, PreWarming = PreWarmMode.None, ParallelExecution = false }
                : new BlocksConfig { MinGasPrice = 0 },
            new TxPoolConfig
            {
                FrameTxMaxVerifyGas = _verifyGasCeiling,
                FrameTxSimulationBudgetPerHeadMs = _shedding ? new TxPoolConfig().FrameTxSimulationBudgetPerHeadMs : int.MaxValue,
            },
        ];
    }

    /// <summary>The per-head simulation budget the pool ran with. Only the shedding arms run the node's stock budget;
    /// the others lift it so the flood reaches the simulator, which makes their EVM-shape rows a counterfactual node.</summary>
    private string SimBudgetField =>
        $"sim_budget_per_head_ms={(_shedding ? new TxPoolConfig().FrameTxSimulationBudgetPerHeadMs.ToString(CultureInfo.InvariantCulture) : "unlimited")}";

    private static void Emit(string line)
    {
        string path = Environment.GetEnvironmentVariable("FRAME_FLOOD_OUT")
                      ?? Path.Combine(Path.GetTempPath(), "frame-tx-flood.txt");
        // Recorded on every row so a reader of -results.txt alone, without PROVENANCE.txt, can tell whether
        // the build ran against the stock MAX_VERIFY_GAS or one patched by raise_verify_gas_const.
        string record = $"RESULT {line} max_verify_gas_const={Eip8141Constants.MaxVerifyGas} "
                        + $"block_processing_signal={(s_blockProcessingSignal ? "on" : "off")} "
                        + $"runner={Environment.GetEnvironmentVariable("RUNNER_NAME") ?? "local"}";
        TestContext.Out.WriteLine(record);
        File.AppendAllText(path, record + Environment.NewLine);
    }
}
