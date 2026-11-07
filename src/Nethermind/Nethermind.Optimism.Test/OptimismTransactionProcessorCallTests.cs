// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Db;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Optimism.Test;

public class OptimismTransactionProcessorCallTests
{
    private const ulong L1Cost = 1_000;
    private const ulong OperatorFeePerGas = 10;
    private static readonly Address L1FeeRecipient = TestItem.AddressF;

    [Test]
    public void Call_from_unfunded_sender_is_not_charged_rollup_fees()
    {
        using ProcessorEnvironment context = new();

        context.Processor.SetBlockExecutionContext(context.BlockContext);
        TransactionResult result = context.Processor.CallAndRestore(CreateTransaction(TestItem.AddressC), NullTxTracer.Instance);

        Assert.That(result.TransactionExecuted, Is.True, result.ErrorDescription);
    }

    [Test]
    public void Estimate_gas_from_unfunded_sender_succeeds()
    {
        using ProcessorEnvironment context = new();

        GasEstimation estimation = new GasEstimator(context.Processor, context.State)
            .Estimate(CreateTransaction(TestItem.AddressC), context.BlockContext, errorMargin: 0);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(estimation.Error, Is.Null);
            Assert.That(estimation.Gas, Is.EqualTo(GasCostOf.Transaction));
        }
    }

    // SkipValidationAndCommit is how proof_getTransactionReceipt and retrospective tracing replay mined transactions.
    [TestCase(ExecutionOptions.Commit)]
    [TestCase(ExecutionOptions.SkipValidationAndCommit)]
    public void Mined_transaction_pays_rollup_fees(ExecutionOptions options)
    {
        using ProcessorEnvironment context = new();
        context.State.CreateAccount(TestItem.AddressA, 1.Ether);
        context.State.Commit(Prague.Instance);
        UInt256 operatorCost = (UInt256)GasCostOf.Transaction * OperatorFeePerGas;

        context.Processor.SetBlockExecutionContext(context.BlockContext);
        TransactionResult result = context.Processor.Process(CreateTransaction(TestItem.AddressA), NullTxTracer.Instance, options);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(TransactionResult.Ok));
            Assert.That(context.State.GetBalance(L1FeeRecipient), Is.EqualTo((UInt256)L1Cost + 1));
            Assert.That(context.State.GetBalance(PreDeploys.OperatorFeeRecipient), Is.EqualTo(operatorCost + 1));
        }
    }

    [TestCase(ExecutionOptions.Commit)]
    [TestCase(ExecutionOptions.SkipValidationAndCommit)]
    [TestCase(ExecutionOptions.Warmup | ExecutionOptions.SkipValidation)]
    public void Transaction_from_unfunded_sender_is_rejected_outside_calls(ExecutionOptions options)
    {
        using ProcessorEnvironment context = new();

        context.Processor.SetBlockExecutionContext(context.BlockContext);
        TransactionResult result = context.Processor.Process(CreateTransaction(TestItem.AddressC), NullTxTracer.Instance, options);

        Assert.That(result, Is.EqualTo(TransactionResult.InsufficientSenderBalance));
    }

    private static Transaction CreateTransaction(Address sender) => Build.A.Transaction
        .WithSenderAddress(sender)
        .WithTo(TestItem.AddressB)
        .WithValue(0)
        .WithGasPrice(0)
        .WithGasLimit(100_000)
        .TestObject;

    private sealed class ProcessorEnvironment : IDisposable
    {
        private readonly IDisposable _scope;

        public ProcessorEnvironment()
        {
            ISpecProvider specProvider = new TestSpecProvider(Prague.Instance);
            State = TestWorldStateFactory.CreateForTest(TestMemDbProvider.Init(), LimboLogs.Instance);
            _scope = State.BeginScope(IWorldState.PreGenesis);
            State.CreateAccount(L1FeeRecipient, 1);
            State.CreateAccount(PreDeploys.OperatorFeeRecipient, 1);
            State.Commit(Prague.Instance);

            ICostHelper costHelper = Substitute.For<ICostHelper>();
            costHelper.ComputeL1Cost(Arg.Any<Transaction>(), Arg.Any<BlockHeader>(), Arg.Any<IWorldState>()).Returns((UInt256)L1Cost);
            costHelper.ComputeOperatorCost(Arg.Any<ulong>(), Arg.Any<BlockHeader>(), Arg.Any<IWorldState>())
                .Returns(static call => (UInt256)call.ArgAt<ulong>(0) * OperatorFeePerGas);

            IOptimismSpecHelper specHelper = Substitute.For<IOptimismSpecHelper>();
            specHelper.IsBedrock(Arg.Any<BlockHeader>()).Returns(true);
            specHelper.IsRegolith(Arg.Any<BlockHeader>()).Returns(true);
            specHelper.IsIsthmus(Arg.Any<BlockHeader>()).Returns(true);
            specHelper.L1FeeReceiver.Returns(L1FeeRecipient);

            Processor = new OptimismTransactionProcessor(BlobBaseFeeCalculator.Instance, specProvider, State,
                new EthereumVirtualMachine(new TestBlockhashProvider(specProvider), specProvider, LimboLogs.Instance),
                LimboLogs.Instance, costHelper, specHelper, new EthereumCodeInfoRepository(State));
            BlockContext = new BlockExecutionContext(
                Build.A.BlockHeader.WithNumber(1).WithBaseFee(0).WithGasLimit(30_000_000).TestObject, Prague.Instance);
        }

        public IWorldState State { get; }
        public OptimismTransactionProcessor Processor { get; }
        public BlockExecutionContext BlockContext { get; }

        public void Dispose() => _scope.Dispose();
    }
}
