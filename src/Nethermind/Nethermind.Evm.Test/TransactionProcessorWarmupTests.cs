// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

public class TransactionProcessorWarmupTests
{
    private ISpecProvider _specProvider = null!;
    private IEthereumEcdsa _ethereumEcdsa = null!;
    private ITransactionProcessor _transactionProcessor = null!;
    private IWorldState _stateProvider = null!;
    private IDisposable _worldStateCloser = null!;

    [SetUp]
    public void Setup()
    {
        _specProvider = new TestSpecProvider(Prague.Instance);
        _stateProvider = TestWorldStateFactory.CreateForTest();
        _worldStateCloser = _stateProvider.BeginScope(IWorldState.PreGenesis);
        EthereumCodeInfoRepository codeInfoRepository = new(_stateProvider);
        EthereumVirtualMachine virtualMachine = new(new TestBlockhashProvider(_specProvider), _specProvider, LimboLogs.Instance);
        _transactionProcessor = new EthereumTransactionProcessor(BlobBaseFeeCalculator.Instance, _specProvider, _stateProvider, virtualMachine, codeInfoRepository, LimboLogs.Instance);
        _ethereumEcdsa = new EthereumEcdsa(_specProvider.ChainId);
    }

    [TearDown]
    public void TearDown() => _worldStateCloser?.Dispose();

    [Test]
    public void Unobserved_logs_preserve_warmup_gas_and_memory([Range(0, 4)] int topicCount, [Values] bool receiptTracer)
    {
        Hash256[] topics = new Hash256[topicCount];
        Array.Fill(topics, TestItem.KeccakA);
        byte[] code = Prepare.EvmCode.Log(128, 1024, topics)
            .Op(Instruction.MSIZE).PushData(0).Op(Instruction.SSTORE).Done;
        (Transaction tx, Snapshot snapshot) = PrepareLogTransaction(code, 500_000);
        using LogCaptureTracer tracer = new(receiptTracer);

        _transactionProcessor.Warmup(tx, tracer);
        UInt256 expectedBalance = _stateProvider.GetBalance(TestItem.AddressA);
        _stateProvider.Restore(snapshot);
        TransactionResult result = _transactionProcessor.Warmup(tx, NullTxTracer.Instance);

        Assert.That(tracer.Logs, Has.Count.EqualTo(1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TransactionExecuted, Is.True);
            Assert.That(_stateProvider.GetBalance(TestItem.AddressA), Is.EqualTo(expectedBalance));
            Assert.That(_stateProvider.GetNonce(TestItem.AddressA), Is.EqualTo(1UL));
            Assert.That(new UInt256(_stateProvider.Get(new StorageCell(TestItem.AddressB, 0)), isBigEndian: true), Is.EqualTo(new UInt256(1152)));
            Assert.That(tracer.Logs[0].Topics, Is.EqualTo(topics));
            Assert.That(tracer.Logs[0].Data, Is.EqualTo(new byte[128]));
        }
    }

    private static IEnumerable<TestCaseData> FailedLogs()
    {
        yield return new TestCaseData(Prepare.EvmCode.PushData(0).PushData(0).Op(Instruction.LOG4).Done, 500_000UL)
            .SetName("Unobserved_log_checks_topic_stack_underflow");
        yield return new TestCaseData(Prepare.EvmCode.PushData(1).PushData(UInt256.MaxValue).Op(Instruction.LOG0).Done, 500_000UL)
            .SetName("Unobserved_log_checks_memory_overflow");
        yield return new TestCaseData(Prepare.EvmCode.Log(1024, 0).Done, 22_000UL)
            .SetName("Unobserved_log_checks_emission_gas");
    }

    [TestCaseSource(nameof(FailedLogs))]
    public void Unobserved_logs_preserve_failure(byte[] code, ulong gasLimit)
    {
        (Transaction tx, Snapshot snapshot) = PrepareLogTransaction(code, gasLimit);
        using LogCaptureTracer tracer = new(receiptTracer: true);
        _transactionProcessor.Warmup(tx, tracer);
        Assert.That(tracer.Failed, Is.True);
        _stateProvider.Restore(snapshot);

        TransactionResult result = _transactionProcessor.Warmup(tx, NullTxTracer.Instance);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TransactionExecuted, Is.True);
            Assert.That(_stateProvider.GetBalance(TestItem.AddressA), Is.EqualTo(1.Ether - gasLimit));
            Assert.That(_stateProvider.GetNonce(TestItem.AddressA), Is.EqualTo(1UL));
        }
    }

    [Test]
    public void Unobserved_logs_still_fail_in_static_calls()
    {
        _stateProvider.CreateAccount(TestItem.AddressC, 0);
        _stateProvider.InsertCode(TestItem.AddressC, Prepare.EvmCode.Log(0, 0).Done, _specProvider.GenesisSpec);
        byte[] code = Prepare.EvmCode.StaticCall(TestItem.AddressC, 50_000).Op(Instruction.ISZERO)
            .PushData(0).Op(Instruction.SSTORE).Done;
        (Transaction tx, _) = PrepareLogTransaction(code, 500_000);

        _transactionProcessor.Warmup(tx, NullTxTracer.Instance);

        Assert.That(new UInt256(_stateProvider.Get(new StorageCell(TestItem.AddressB, 0)), isBigEndian: true), Is.EqualTo(UInt256.One));
    }

    private (Transaction, Snapshot) PrepareLogTransaction(byte[] code, ulong gasLimit)
    {
        _stateProvider.CreateAccount(TestItem.AddressA, 1.Ether);
        _stateProvider.CreateAccount(TestItem.AddressB, 0);
        _stateProvider.InsertCode(TestItem.AddressB, code, _specProvider.GenesisSpec);
        _stateProvider.Commit(_specProvider.GenesisSpec);
        _stateProvider.CommitTree(0);
        Snapshot snapshot = _stateProvider.TakeSnapshot();
        Transaction tx = Build.A.Transaction.WithTo(TestItem.AddressB).WithGasLimit(gasLimit)
            .WithGasPrice(1).SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA).TestObject;
        BlockHeader header = Build.A.BlockHeader.WithNumber(1).WithBaseFee(0).TestObject;
        _transactionProcessor.SetBlockExecutionContext(header);
        return (tx, snapshot);
    }

    private sealed class LogCaptureTracer : TxTracer
    {
        public List<LogEntry> Logs { get; } = [];
        public bool Failed { get; private set; }

        public LogCaptureTracer(bool receiptTracer)
        {
            IsTracingReceipt = receiptTracer;
            IsTracingLogs = !receiptTracer;
        }

        public override void ReportLog(LogEntry log) => Logs.Add(log);

        public override void MarkAsSuccess(Address recipient, in GasConsumed gasSpent, byte[] output, LogEntry[] logs, Hash256? stateRoot = null)
            => Logs.AddRange(logs);

        public override void MarkAsFailed(Address recipient, in GasConsumed gasSpent, byte[] output, string? error, Hash256? stateRoot = null)
            => Failed = true;
    }

    // Warmup must take the real execution path: no-op fee/nonce handling made same-sender
    // warm sequences run with undebited balances and unbumped nonces, so deploy chains
    // computed wrong CREATE addresses and warmed the wrong state.
    [Test]
    public void Warmup_InTheThrowawayScope_DebitsFeesAndBumpsTheNonce()
    {
        _stateProvider.CreateAccount(TestItem.AddressA, 1.Ether);
        _stateProvider.Commit(_specProvider.GenesisSpec);
        _stateProvider.CommitTree(0);

        Transaction tx = Build.A.Transaction
            .WithGasPrice(1)
            .WithMaxFeePerGas(1)
            .WithTo(TestItem.AddressB)
            .WithValue(100.GWei)
            .WithGasLimit(100_000)
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA)
            .TestObject;
        Block block = Build.A.Block
            .WithNumber(long.MaxValue)
            .WithTimestamp(MainnetSpecProvider.PragueBlockTimestamp)
            .WithTransactions(tx)
            .WithGasLimit(10_000_000)
            .TestObject;
        UInt256 balanceBefore = _stateProvider.GetBalance(TestItem.AddressA);

        _transactionProcessor.SetBlockExecutionContext(new BlockExecutionContext(block.Header, _specProvider.GetSpec(block.Header)));
        TransactionResult result = _transactionProcessor.Warmup(tx, NullTxTracer.Instance);

        Assert.That(result.TransactionExecuted, Is.True, "precondition: the warm execution ran");
        Assert.That(_stateProvider.GetNonce(TestItem.AddressA), Is.EqualTo(1UL),
            "a warm execution must bump the nonce so a same-sender successor warms the right state");
        Assert.That(_stateProvider.GetBalance(TestItem.AddressA), Is.EqualTo(balanceBefore - 100.GWei - 21_000),
            "a warm execution must debit value and gas so successors see real balances");
        Assert.That(tx.BlockGasUsed, Is.EqualTo(100_000UL),
            "warmup must never mutate the shared transaction object: the getter must still fall back to the gas limit");
    }

    // A sender funded earlier in the block by another sender's transaction has no balance in
    // the parent state; per-sender warm groups cannot see that funding, so the warm pass must
    // execute best-effort instead of losing the sender's warming to the balance check.
    [Test]
    public void Warmup_ForASenderWithoutParentStateBalance_StillExecutes()
    {
        _stateProvider.CreateAccount(TestItem.AddressA, UInt256.Zero);
        _stateProvider.Commit(_specProvider.GenesisSpec);
        _stateProvider.CommitTree(0);

        Transaction tx = Build.A.Transaction
            .WithGasPrice(1)
            .WithMaxFeePerGas(1)
            .WithTo(TestItem.AddressB)
            .WithGasLimit(100_000)
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA)
            .TestObject;
        Block block = Build.A.Block
            .WithNumber(long.MaxValue)
            .WithTimestamp(MainnetSpecProvider.PragueBlockTimestamp)
            .WithTransactions(tx)
            .WithGasLimit(10_000_000)
            .TestObject;

        _transactionProcessor.SetBlockExecutionContext(new BlockExecutionContext(block.Header, _specProvider.GetSpec(block.Header)));
        TransactionResult result = _transactionProcessor.Warmup(tx, NullTxTracer.Instance);

        Assert.That(result.TransactionExecuted, Is.True,
            "an underfunded warm sender must still warm its execution path");
        Assert.That(_stateProvider.GetNonce(TestItem.AddressA), Is.EqualTo(1UL));
    }

    // The motivating bug: with no-op nonce handling, every deploy in a same-sender warm chain
    // computed the same CREATE address and warmed the wrong storage. With real semantics each
    // deploy must land where the real execution will put it.
    [Test]
    public void Warmup_ForASameSenderDeployChain_DeploysAtConsecutiveCreateAddresses()
    {
        _stateProvider.CreateAccount(TestItem.AddressA, 1.Ether);
        _stateProvider.Commit(_specProvider.GenesisSpec);
        _stateProvider.CommitTree(0);

        Transaction firstDeploy = Build.A.Transaction
            .WithGasPrice(1)
            .WithMaxFeePerGas(1)
            .WithTo(null)
            .WithNonce(0)
            .WithGasLimit(100_000)
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA)
            .TestObject;
        Transaction secondDeploy = Build.A.Transaction
            .WithGasPrice(1)
            .WithMaxFeePerGas(1)
            .WithTo(null)
            .WithNonce(1)
            .WithGasLimit(100_000)
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA)
            .TestObject;
        Block block = Build.A.Block
            .WithNumber(long.MaxValue)
            .WithTimestamp(MainnetSpecProvider.PragueBlockTimestamp)
            .WithTransactions(firstDeploy, secondDeploy)
            .WithGasLimit(10_000_000)
            .TestObject;

        _transactionProcessor.SetBlockExecutionContext(new BlockExecutionContext(block.Header, _specProvider.GetSpec(block.Header)));
        _transactionProcessor.Warmup(firstDeploy, NullTxTracer.Instance);
        _transactionProcessor.Warmup(secondDeploy, NullTxTracer.Instance);

        Assert.That(_stateProvider.AccountExists(ContractAddress.From(TestItem.AddressA, 0)), Is.True,
            "the first deploy must land at the nonce-0 CREATE address");
        Assert.That(_stateProvider.AccountExists(ContractAddress.From(TestItem.AddressA, 1)), Is.True,
            "the successor must observe the bumped nonce and deploy at the nonce-1 CREATE address");
    }
}
