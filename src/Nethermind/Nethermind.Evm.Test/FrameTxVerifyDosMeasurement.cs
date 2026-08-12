// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

/// <summary>
/// Measures the unpaid wall-clock cost of the EIP-8141 validation prefix when a transaction names a
/// solvent payer but never reaches APPROVE: every node runs the whole verification budget and then
/// drops the transaction, so no fee is collected for the work.
/// </summary>
/// <remarks>
/// A measurement harness for sizing MAX_VERIFY_GAS, not an assertion of behaviour. Each case first
/// proves the whole budget is actually consumed, then reports several independent repeats so that
/// run-to-run spread is visible in the output rather than hidden behind a single median.
/// </remarks>
[TestFixture]
[Explicit("measurement harness")]
public class FrameTxVerifyDosMeasurement
{
    private const long BlockGasLimit = 30_000_000;
    private const int Warmup = 300;
    private const int Samples = 200;
    private const int Repeats = 5;

    private static readonly Address Sender = TestItem.AddressA;

    private ISpecProvider _specProvider;
    private ITransactionProcessor _transactionProcessor;
    private IWorldState _stateProvider;
    private IDisposable _worldStateCloser;

    private IReleaseSpec Spec => _specProvider.GenesisSpec;

    /// <summary>Records how much gas the verification frame actually burned before the transaction was dropped.</summary>
    private sealed class BudgetProbe : TxTracer
    {
        public BudgetProbe() => IsTracingInstructions = true;

        public ulong HighestRemaining { get; private set; }
        public ulong LowestRemaining { get; private set; } = ulong.MaxValue;
        public long Operations { get; private set; }
        public EvmExceptionType? LastError { get; private set; }

        public ulong Consumed => HighestRemaining >= LowestRemaining ? HighestRemaining - LowestRemaining : 0;

        public override void StartOperation(int pc, Instruction opcode, ulong gas, in ExecutionEnvironment env)
        {
            if (gas > HighestRemaining) HighestRemaining = gas;
            if (gas < LowestRemaining) LowestRemaining = gas;
            Operations++;
        }

        public override void ReportOperationError(EvmExceptionType error) => LastError = error;
    }

    [SetUp]
    public void Setup()
    {
        _specProvider = new TestSpecProvider(Eip8141Prototype.Instance);
        _stateProvider = TestWorldStateFactory.CreateForTest();
        _worldStateCloser = _stateProvider.BeginScope(IWorldState.PreGenesis);
        EthereumCodeInfoRepository codeInfoRepository = new(_stateProvider);
        EthereumVirtualMachine virtualMachine = new(new TestBlockhashProvider(_specProvider), _specProvider, LimboLogs.Instance);
        _transactionProcessor = new EthereumTransactionProcessor(BlobBaseFeeCalculator.Instance, _specProvider, _stateProvider, virtualMachine, codeInfoRepository, LimboLogs.Instance);
    }

    [TearDown]
    public void TearDown() => _worldStateCloser?.Dispose();

    /// <summary>Cheapest gas per dispatch: a bare jump loop.</summary>
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

    /// <summary>Memory-expanding hashing loop: the worst realtime-per-gas shape we can reach cheaply.</summary>
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

    [TestCase(300_000L, "jump")]
    [TestCase(500_000L, "jump")]
    [TestCase(1_048_576L, "jump")]
    [TestCase(300_000L, "keccak")]
    [TestCase(500_000L, "keccak")]
    [TestCase(1_048_576L, "keccak")]
    [TestCase(300_000L, "keccak-wide")]
    [TestCase(500_000L, "keccak-wide")]
    [TestCase(1_048_576L, "keccak-wide")]
    public void UnpaidVerificationCost(long verifyGas, string shape)
    {
        byte[] code = shape switch
        {
            "jump" => JumpLoop(),
            "keccak" => KeccakLoop(),
            "keccak-wide" => KeccakWideLoop(),
            _ => throw new ArgumentOutOfRangeException(nameof(shape))
        };

        _stateProvider.CreateAccount(Sender, 1.Ether);
        _stateProvider.InsertCode(Sender, code, Spec);
        _stateProvider.Commit(Spec);
        _stateProvider.CommitTree(0);

        // Without these two facts the timings below mean nothing: the transaction must be dropped,
        // and it must be dropped only after the whole verification budget has been burned.
        BudgetProbe probe = new();
        TransactionResult probeResult = Process(AttackTx(verifyGas), probe);
        Assert.That(probeResult.TransactionExecuted, Is.False, "the attack transaction must not settle");
        string diag = $"ops={probe.Operations} high={probe.HighestRemaining} low={probe.LowestRemaining} err={probe.LastError}";
        Assert.That(probe.LastError, Is.EqualTo(EvmExceptionType.OutOfGas), $"the frame must end by exhausting its budget ({diag})");
        Assert.That((long)probe.Consumed, Is.GreaterThan(verifyGas * 99 / 100), $"nearly the whole verification budget must be burned ({diag})");

        for (int i = 0; i < Warmup; i++) Process(AttackTx(verifyGas));

        List<double> medians = new(Repeats);
        for (int repeat = 0; repeat < Repeats; repeat++)
        {
            List<double> perTxMicros = new(Samples);
            for (int i = 0; i < Samples; i++)
            {
                Transaction tx = AttackTx(verifyGas);
                long start = Stopwatch.GetTimestamp();
                Process(tx);
                perTxMicros.Add(Stopwatch.GetElapsedTime(start).TotalMicroseconds);
            }
            perTxMicros.Sort();
            medians.Add(perTxMicros[perTxMicros.Count / 2]);
        }

        medians.Sort();
        double best = medians[0];
        double median = medians[medians.Count / 2];
        double worst = medians[^1];
        double spreadPercent = (worst - best) / best * 100.0;

        long gasPerTx = Eip8141Constants.IntrinsicGasCost + Eip8141Constants.PerFrameGasCost + verifyGas;
        long txPerBlock = BlockGasLimit / gasPerTx;

        string line =
            $"RESULT shape={shape} verify_gas={verifyGas} " +
            $"consumed_gas={probe.Consumed} ops={probe.Operations} " +
            $"best_us={best:F1} median_us={median:F1} worst_us={worst:F1} spread_pct={spreadPercent:F1} " +
            $"us_per_Mgas={best * 1_000_000 / verifyGas:F1} " +
            $"tx_per_block={txPerBlock} unpaid_block_ms={best * txPerBlock / 1000.0:F1}";
        TestContext.Out.WriteLine(line);
        System.IO.File.AppendAllText("/tmp/frame-dos-results.txt", line + System.Environment.NewLine);
    }

    /// <summary>
    /// The unpaid gas one block-production attempt burns on a prefix that never approves, without the
    /// timing loops: this is the per-attempt multiplicand for the pool-retention count measured in
    /// <c>Nethermind.TxPool.Test/FrameTxPrefixRetryMeasurement</c>.
    /// </summary>
    [TestCase(100_000L, TestName = "burn at the spec default budget")]
    [TestCase(236_285L, TestName = "burn at the measured pool prefix")]
    public void UnpaidBurnPerAttempt(long verifyGas)
    {
        _stateProvider.CreateAccount(Sender, 1.Ether);
        _stateProvider.InsertCode(Sender, JumpLoop(), Spec);
        _stateProvider.Commit(Spec);
        _stateProvider.CommitTree(0);

        BudgetProbe probe = new();
        TransactionResult result = Process(AttackTx(verifyGas), probe);

        string path = Environment.GetEnvironmentVariable("FRAME_RETRY_OUT")
                      ?? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "frame-prefix-retry.txt");
        System.IO.File.AppendAllText(path,
            $"RESULT case=burn_per_attempt budget={verifyGas} consumed={probe.Consumed} ops={probe.Operations} "
            + $"settled={result.TransactionExecuted} err={probe.LastError}{Environment.NewLine}");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TransactionExecuted, Is.False, "the attack transaction must not settle, or the work is paid for");
            Assert.That(probe.LastError, Is.EqualTo(EvmExceptionType.OutOfGas), "the frame must end by exhausting its budget");
            Assert.That((long)probe.Consumed, Is.GreaterThan(verifyGas * 99 / 100), "nearly the whole budget must be burned");
        }
    }

    private TransactionResult Process(Transaction tx, ITxTracer? tracer = null)
    {
        Block block = Build.A.Block.WithNumber(1)
            .WithBaseFeePerGas(UInt256.Zero)
            .WithBeneficiary(TestItem.AddressE)
            .WithTransactions(tx)
            .WithGasLimit(BlockGasLimit).TestObject;
        return _transactionProcessor.Execute(tx, new BlockExecutionContext(block.Header, Spec), tracer ?? NullTxTracer.Instance);
    }

    private static Transaction AttackTx(long verifyGas) =>
        new()
        {
            Type = TxType.FrameTx,
            ChainId = TestBlockchainIds.ChainId,
            Nonce = 0,
            SenderAddress = Sender,
            Frames = [new TxFrame(TxFrame.ModeVerify, TxFrame.ApproveExecutionAndPayment, target: null, gasLimit: (ulong)verifyGas, UInt256.Zero, default)],
            FrameSignatures = [],
            GasPrice = 1,
            DecodedMaxFeePerGas = 1,
        };
}
