// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
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
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.Precompiles;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

public partial class TransactionProcessorTests
{
    private static readonly byte[] StopCode = [(byte)Instruction.STOP];

    [TestCaseSource(nameof(SimpleTransferFastPathCases))]
    public void Simple_transfer_fast_path_predicate_enters_vm_when_expected(SimpleTransferFastPathCase testCase)
    {
        IReleaseSpec spec = Prague.Instance;
        Address recipient = testCase.CreateRecipient(_stateProvider, spec);
        _stateProvider.Commit(spec);
        _stateProvider.CommitTree(0);

        (CountingVirtualMachine virtualMachine, EthereumTransactionProcessor transactionProcessor) = CreateProcessor(_specProvider);

        Transaction tx = BuildSimpleTransfer(recipient, testCase.Value, testCase.WithAuthorizationList);
        Block block = BuildPragueBlock(tx);

        UInt256 senderBalanceBefore = _stateProvider.GetBalance(TestItem.AddressA);
        UInt256 recipientBalanceBefore = _stateProvider.GetBalance(recipient);

        TransactionResult result = transactionProcessor.Execute(tx, new BlockExecutionContext(block.Header, spec), NullTxTracer.Instance);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TransactionExecuted, Is.True);
            Assert.That(virtualMachine.ExecuteTransactionCalls, Is.EqualTo(testCase.ExpectedVmCalls));
            Assert.That(_stateProvider.GetNonce(TestItem.AddressA), Is.EqualTo((UInt256)1));
            Assert.That(tx.SpentGas, Is.EqualTo(testCase.ExpectedSpentGas));
            Assert.That(_stateProvider.GetBalance(TestItem.AddressA), Is.EqualTo(senderBalanceBefore - testCase.ExpectedSenderDebit));
            if (recipient != TestItem.AddressA)
            {
                Assert.That(_stateProvider.GetBalance(recipient), Is.EqualTo(recipientBalanceBefore + testCase.Value));
            }
        }
    }

    [Test]
    public void Simple_transfer_fast_path_reports_action_trace()
    {
        IReleaseSpec spec = Prague.Instance;
        Address recipient = CreateEmptyCodeRecipient(_stateProvider, spec, 1400);
        _stateProvider.Commit(spec);
        _stateProvider.CommitTree(0);

        (CountingVirtualMachine virtualMachine, EthereumTransactionProcessor transactionProcessor) = CreateProcessor(_specProvider);

        Transaction tx = BuildSimpleTransfer(recipient, 7.Wei, withAuthorizationList: false);
        Block block = BuildPragueBlock(tx);
        SimpleTransferActionTracer tracer = new();

        TransactionResult result = transactionProcessor.Execute(tx, new BlockExecutionContext(block.Header, spec), tracer);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TransactionExecuted, Is.True);
            Assert.That(virtualMachine.ExecuteTransactionCalls, Is.EqualTo(0));
            Assert.That(tracer.ActionCalls, Is.EqualTo(1));
            Assert.That(tracer.ActionEndCalls, Is.EqualTo(1));
            Assert.That(tracer.ActionGas, Is.EqualTo(tx.GasLimit - GasCostOf.Transaction));
            Assert.That(tracer.ActionEndGas, Is.EqualTo(tx.GasLimit - GasCostOf.Transaction));
            Assert.That(tracer.ActionValue, Is.EqualTo((UInt256)7.Wei));
            Assert.That(tracer.ActionFrom, Is.EqualTo(TestItem.AddressA));
            Assert.That(tracer.ActionTo, Is.EqualTo(recipient));
            Assert.That(tracer.ActionInput, Is.Empty);
            Assert.That(tracer.ActionType, Is.EqualTo(ExecutionType.TRANSACTION));
            Assert.That(tracer.IsPrecompileCall, Is.False);
            Assert.That(tracer.ActionOutput, Is.Empty);
        }
    }

    [Test]
    public void Simple_transfer_fast_path_restores_state_on_call_and_restore()
    {
        Address recipient = Address.FromNumber((UInt256)1401);
        IReleaseSpec spec = Prague.Instance;
        _stateProvider.Commit(spec);
        _stateProvider.CommitTree(0);

        (CountingVirtualMachine virtualMachine, EthereumTransactionProcessor transactionProcessor) = CreateProcessor(_specProvider);

        Transaction tx = BuildSimpleTransfer(recipient, 7.Wei, withAuthorizationList: false);
        Block block = BuildPragueBlock(tx);
        UInt256 senderBalanceBefore = _stateProvider.GetBalance(TestItem.AddressA);
        UInt256 senderNonceBefore = _stateProvider.GetNonce(TestItem.AddressA);

        TransactionResult result = transactionProcessor.CallAndRestore(tx, new BlockExecutionContext(block.Header, spec), NullTxTracer.Instance);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TransactionExecuted, Is.True);
            Assert.That(virtualMachine.ExecuteTransactionCalls, Is.EqualTo(0));
            Assert.That(_stateProvider.GetBalance(TestItem.AddressA), Is.EqualTo(senderBalanceBefore));
            Assert.That(_stateProvider.GetNonce(TestItem.AddressA), Is.EqualTo(senderNonceBefore));
            Assert.That(_stateProvider.AccountExists(recipient), Is.False);
        }
    }

    [TestCase(false, 1ul, true, true)]
    [TestCase(false, 1ul, false, true)]
    [TestCase(false, 0ul, true, false)]
    [TestCase(true, 1ul, true, false)]
    public void Simple_transfer_fast_path_reports_eip7708_log_only_for_non_zero_transfer_to_different_account(
        bool senderIsRecipient,
        ulong value,
        bool isTracingLogs,
        bool expectTransferLog)
    {
        Address recipient = senderIsRecipient ? TestItem.AddressA : Address.FromNumber((UInt256)1300);
        IReleaseSpec spec = Amsterdam.Instance;
        ISpecProvider specProvider = new TestSpecProvider(spec);
        _stateProvider.Commit(spec);
        _stateProvider.CommitTree(0);

        (CountingVirtualMachine virtualMachine, EthereumTransactionProcessor transactionProcessor) = CreateProcessor(specProvider);

        Transaction tx = BuildSimpleTransfer(recipient, (UInt256)value, withAuthorizationList: false);
        Block block = Build.A.Block.WithNumber(1).WithTransactions(tx).WithGasLimit(1_000_000).TestObject;
        SimpleTransferLogTracer tracer = new(isTracingLogs);

        TransactionResult result = transactionProcessor.Execute(tx, new BlockExecutionContext(block.Header, spec), tracer);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TransactionExecuted, Is.True);
            Assert.That(virtualMachine.ExecuteTransactionCalls, Is.EqualTo(0));
            Assert.That(tracer.ReceiptLogs, Has.Length.EqualTo(expectTransferLog ? 1 : 0));
            Assert.That(tracer.ReportLogCalls, Is.EqualTo(expectTransferLog && isTracingLogs ? 1 : 0));
        }
        if (expectTransferLog)
        {
            AssertLog(tracer.ReceiptLogs[0], ExpectedTransferLog(TestItem.AddressA, recipient, value));
            if (isTracingLogs)
            {
                AssertLog(tracer.ReportedLogs[0], ExpectedTransferLog(TestItem.AddressA, recipient, value));
            }
        }
    }

    // EIP-8037: a value transfer that materialises a new (dead) recipient on the EVM path
    // (an authorization-list tx bypasses the simple-transfer fast path) must pay NEW_ACCOUNT
    // state gas exactly once, mirroring the ExecuteSimpleTransfer charge.
    [Test]
    public void Eip8037_evm_path_value_transfer_to_dead_recipient_charges_new_account_state_gas()
    {
        Address liveRecipient = Address.FromNumber((UInt256)2100);
        _stateProvider.CreateAccount(liveRecipient, 1); // exists -> not dead -> no NEW_ACCOUNT charge
        _stateProvider.Commit(Amsterdam.Instance);
        _stateProvider.CommitTree(0);

        (CountingVirtualMachine virtualMachine, EthereumTransactionProcessor transactionProcessor) = CreateProcessor(_specProvider);

        Address deadRecipient = Address.FromNumber((UInt256)2101);
        Transaction liveTx = BuildSetCodeTransfer(liveRecipient, 1.Wei, TestItem.PrivateKeyA, TestItem.PrivateKeyB, 0);
        Transaction deadTx = BuildSetCodeTransfer(deadRecipient, 1.Wei, TestItem.PrivateKeyA, TestItem.PrivateKeyD, 1);

        Block block = BuildAmsterdamBlock(liveTx, deadTx);
        IReleaseSpec spec = _specProvider.GetSpec(block.Header);

        TransactionResult liveResult = transactionProcessor.Execute(liveTx, new BlockExecutionContext(block.Header, spec), NullTxTracer.Instance);
        TransactionResult deadResult = transactionProcessor.Execute(deadTx, new BlockExecutionContext(block.Header, spec), NullTxTracer.Instance);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(spec.IsEip8037Enabled, Is.True);
            Assert.That(liveResult.TransactionExecuted, Is.True);
            Assert.That(deadResult.TransactionExecuted, Is.True);
            Assert.That(virtualMachine.ExecuteTransactionCalls, Is.EqualTo(2)); // both took the EVM path
            Assert.That(_stateProvider.GetBalance(deadRecipient), Is.EqualTo((UInt256)1));
            Assert.That(deadTx.SpentGas - liveTx.SpentGas, Is.EqualTo(GasCostOf.NewAccountState));
        }
    }

    private static Block BuildAmsterdamBlock(params Transaction[] txs) =>
        Build.A.Block
            .WithNumber(MainnetSpecProvider.ParisBlockNumber)
            .WithTimestamp(MainnetSpecProvider.AmsterdamBlockTimestamp)
            .WithTransactions(txs)
            .WithGasLimit(30_000_000)
            .TestObject;

    private Transaction BuildSetCodeTransfer(Address recipient, UInt256 value, PrivateKey sender, PrivateKey authority, UInt256 nonce) =>
        Build.A.Transaction
            .WithType(TxType.SetCode)
            .WithTo(recipient)
            .WithValue(value)
            .WithGasPrice(1)
            .WithGasLimit(1_000_000)
            .WithNonce(nonce)
            .WithAuthorizationCode(_ethereumEcdsa.Sign(authority, _specProvider.ChainId, Address.Zero, 0))
            .SignedAndResolved(_ethereumEcdsa, sender, eip155Enabled)
            .TestObject;

    private (CountingVirtualMachine Vm, EthereumTransactionProcessor Processor) CreateProcessor(ISpecProvider specProvider)
    {
        CountingVirtualMachine vm = new(new TestBlockhashProvider(specProvider), specProvider, LimboLogs.Instance);
        EthereumCodeInfoRepository codeInfoRepository = new(_stateProvider);
        EthereumTransactionProcessor processor = new(
            BlobBaseFeeCalculator.Instance,
            specProvider,
            _stateProvider,
            vm,
            codeInfoRepository,
            LimboLogs.Instance);
        return (vm, processor);
    }

    private static Block BuildPragueBlock(Transaction tx) =>
        Build.A.Block
            .WithNumber(MainnetSpecProvider.PragueActivation.BlockNumber)
            .WithTimestamp(MainnetSpecProvider.PragueBlockTimestamp)
            .WithTransactions(tx)
            .WithGasLimit(1_000_000)
            .TestObject;

    public static IEnumerable<TestCaseData> SimpleTransferFastPathCases()
    {
        yield return new TestCaseData(new SimpleTransferFastPathCase((state, spec) => CreateEmptyCodeRecipient(state, spec, 1100), 1.Wei, false, 0, GasCostOf.Transaction, 1.Wei + GasCostOf.Transaction))
            .SetName("Empty-code recipient with value uses fast path");
        yield return new TestCaseData(new SimpleTransferFastPathCase((state, spec) => CreateEmptyCodeRecipient(state, spec, 1100), UInt256.Zero, false, 0, GasCostOf.Transaction, GasCostOf.Transaction))
            .SetName("Empty-code recipient with zero value uses fast path");
        yield return new TestCaseData(new SimpleTransferFastPathCase((_, _) => TestItem.AddressA, 1.Wei, false, 0, GasCostOf.Transaction, GasCostOf.Transaction))
            .SetName("Self-send with value uses fast path and only charges gas");
        yield return new TestCaseData(new SimpleTransferFastPathCase((state, spec) => CreateContractRecipient(state, spec, 1101), 1.Wei, false, 1, GasCostOf.Transaction, 1.Wei + GasCostOf.Transaction))
            .SetName("Contract recipient enters VM");
        yield return new TestCaseData(new SimpleTransferFastPathCase((_, _) => IdentityPrecompile.Address, 1.Wei, false, 1, GasCostOf.Transaction + 15, 1.Wei + GasCostOf.Transaction + 15))
            .SetName("Precompile recipient enters VM");
        yield return new TestCaseData(new SimpleTransferFastPathCase((state, spec) => CreateEmptyCodeRecipient(state, spec, 1100), 1.Wei, true, 1, GasCostOf.Transaction + 25_000, 1.Wei))
            .SetName("Authorization-list transaction enters VM");
        yield return new TestCaseData(new SimpleTransferFastPathCase((state, spec) => CreateDelegatedToContractRecipient(state, spec, 1103), 1.Wei, false, 1, GasCostOf.Transaction, 1.Wei + GasCostOf.Transaction))
            .SetName("Delegated recipient with executable target enters VM");
    }

    private Transaction BuildSimpleTransfer(Address recipient, UInt256 value, bool withAuthorizationList)
    {
        TransactionBuilder<Transaction> builder = Build.A.Transaction
            .WithTo(recipient)
            .WithValue(value)
            .WithGasPrice(1)
            .WithGasLimit(100_000);

        if (withAuthorizationList)
        {
            builder
                .WithType(TxType.SetCode)
                .WithAuthorizationCode(_ethereumEcdsa.Sign(TestItem.PrivateKeyB, _specProvider.ChainId, Address.Zero, 0));
        }

        return builder
            .SignedAndResolved(_ethereumEcdsa, TestItem.PrivateKeyA, eip155Enabled)
            .TestObject;
    }

    private static Address CreateEmptyCodeRecipient(IWorldState state, IReleaseSpec spec, uint addressNumber)
    {
        Address recipient = Address.FromNumber((UInt256)addressNumber);
        state.CreateAccount(recipient, UInt256.Zero);
        CodeInfoRepository.InsertCode(state, ReadOnlyMemory<byte>.Empty, recipient, spec, out _);
        return recipient;
    }

    private static Address CreateContractRecipient(IWorldState state, IReleaseSpec spec, uint addressNumber)
    {
        Address recipient = Address.FromNumber((UInt256)addressNumber);
        state.CreateAccount(recipient, UInt256.Zero);
        CodeInfoRepository.InsertCode(state, StopCode, recipient, spec, out _);
        return recipient;
    }

    private static Address CreateDelegatedToContractRecipient(IWorldState state, IReleaseSpec spec, uint addressNumber)
    {
        Address recipient = Address.FromNumber((UInt256)addressNumber);
        Address codeSource = Address.FromNumber(1200);
        state.CreateAccount(recipient, UInt256.Zero);
        state.CreateAccount(codeSource, UInt256.Zero);
        CodeInfoRepository.InsertCode(state, StopCode, codeSource, spec, out _);
        CodeInfoRepository.SetDelegation(state, codeSource, recipient, spec, out _, out _);
        return recipient;
    }

    public readonly record struct SimpleTransferFastPathCase(
        Func<IWorldState, IReleaseSpec, Address> CreateRecipient,
        UInt256 Value,
        bool WithAuthorizationList,
        int ExpectedVmCalls,
        long ExpectedSpentGas,
        UInt256 ExpectedSenderDebit);

    private static LogEntry ExpectedTransferLog(Address sender, Address recipient, UInt256 value) =>
        new(TransferLog.Sender, value.ToBigEndian(), [TransferLog.TransferSignature, sender.ToHash().ToHash256(), recipient.ToHash().ToHash256()]);

    private static void AssertLog(LogEntry actual, LogEntry expected)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(actual.Address, Is.EqualTo(expected.Address));
            Assert.That(actual.Data, Is.EqualTo(expected.Data));
            Assert.That(actual.Topics, Is.EqualTo(expected.Topics));
        }
    }

    private sealed class SimpleTransferLogTracer(bool isTracingLogs) : TxTracer
    {
        public override bool IsTracingReceipt { get; protected set; } = true;
        public override bool IsTracingLogs { get; protected set; } = isTracingLogs;
        public LogEntry[] ReceiptLogs { get; private set; } = [];
        public List<LogEntry> ReportedLogs { get; } = [];
        public int ReportLogCalls => ReportedLogs.Count;

        public override void MarkAsSuccess(Address recipient, in GasConsumed gasSpent, byte[] output, LogEntry[] logs, Hash256? stateRoot = null) =>
            ReceiptLogs = logs;

        public override void ReportLog(LogEntry log) => ReportedLogs.Add(log);
    }

    private sealed class SimpleTransferActionTracer : TxTracer
    {
        public override bool IsTracingActions { get; protected set; } = true;
        public int ActionCalls { get; private set; }
        public int ActionEndCalls { get; private set; }
        public long ActionGas { get; private set; }
        public long ActionEndGas { get; private set; }
        public UInt256 ActionValue { get; private set; }
        public Address ActionFrom { get; private set; } = Address.Zero;
        public Address ActionTo { get; private set; } = Address.Zero;
        public byte[] ActionInput { get; private set; } = [];
        public ExecutionType ActionType { get; private set; }
        public bool IsPrecompileCall { get; private set; }
        public byte[] ActionOutput { get; private set; } = [];

        public override void ReportAction(long gas, UInt256 value, Address from, Address to, ReadOnlyMemory<byte> input, ExecutionType callType, bool isPrecompileCall = false)
        {
            ActionCalls++;
            ActionGas = gas;
            ActionValue = value;
            ActionFrom = from;
            ActionTo = to;
            ActionInput = input.ToArray();
            ActionType = callType;
            IsPrecompileCall = isPrecompileCall;
        }

        public override void ReportActionEnd(long gas, ReadOnlyMemory<byte> output)
        {
            ActionEndCalls++;
            ActionEndGas = gas;
            ActionOutput = output.ToArray();
        }
    }

    private sealed class CountingVirtualMachine(
        IBlockhashProvider blockHashProvider,
        ISpecProvider specProvider,
        ILogManager logManager)
        : VirtualMachine<EthereumGasPolicy>(blockHashProvider, specProvider, logManager), IVirtualMachine
    {
        public int ExecuteTransactionCalls { get; private set; }

        public override TransactionSubstate ExecuteTransaction<TTracingInst>(
            VmState<EthereumGasPolicy> vmState,
            IWorldState worldState,
            ITxTracer txTracer)
        {
            ExecuteTransactionCalls++;
            return base.ExecuteTransaction<TTracingInst>(vmState, worldState, txTracer);
        }
    }
}
