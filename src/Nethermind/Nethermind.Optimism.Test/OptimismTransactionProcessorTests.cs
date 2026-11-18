// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using Nethermind.Specs.Test.ChainSpecStyle;
using Nethermind.State;
using NUnit.Framework;

namespace Nethermind.Optimism.Test;

public class OptimismTransactionProcessorTests
{
    private const uint OperatorFeeScalar = 2_000;
    private const ulong OperatorFeeConstant = 5_000_000;
    private const ulong TransferGasLimit = 1_000_000;
    private const ulong TransferGasPrice = 10;

    private static readonly UInt256 SenderBalance = 1.Ether;
    private static readonly Address Sender = TestItem.AddressA;
    private static readonly Address Recipient = TestItem.AddressB;
    private static readonly Address Beneficiary = TestItem.AddressC;
    private static readonly Address BaseFeeRecipient = TestItem.AddressD;
    private static readonly Address L1Block = TestItem.AddressE;

    private static readonly TestSpecProvider OperatorFeeSpecProvider =
        new(new OverridableReleaseSpec(Prague.Instance) { FeeCollector = BaseFeeRecipient });

    [TestCase(TxType.DepositTx, TransactionResult.ErrorType.None)]
    [TestCase(TxType.EIP1559, TransactionResult.ErrorType.TransactionSizeOverMaxInitCodeSize)]
    public void Init_code_size_limit_applies_to_non_deposits_only(TxType txType, TransactionResult.ErrorType expectedError)
    {
        IReleaseSpec spec = Prague.Instance;
        using IContainer container = BuildContainer(new OptimismChainSpecEngineParameters { BedrockBlockNumber = 0, RegolithTimestamp = 0 }, spec);
        using ILifetimeScope lifetime = BeginLifetimeScope(container);
        IWorldState state = lifetime.Resolve<IWorldState>();
        using IDisposable scope = state.BeginScope(IWorldState.PreGenesis);

        Transaction tx = Build.A.Transaction
            .WithType(txType)
            .WithSenderAddress(TestItem.AddressA)
            .WithValue(0)
            .WithCode(new byte[spec.MaxInitCodeSize + 1])
            .WithGasLimit(2_000_000)
            .TestObject;
        BlockHeader header = Build.A.BlockHeader.WithGasLimit(30_000_000).TestObject;

        CallOutputTracer tracer = new();

        TransactionResult result = lifetime.Resolve<ITransactionProcessor>()
            .Execute(tx, new BlockExecutionContext(header, spec), tracer);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Error, Is.EqualTo(expectedError), tracer.Error);
            Assert.That(state.AccountExists(ContractAddress.From(TestItem.AddressA, 0)), Is.EqualTo(expectedError == TransactionResult.ErrorType.None), tracer.Error);
        }
    }

    [TestCase(false, 5_000_042ul)]
    [TestCase(true, 4_205_000_000ul)]
    public void Operator_fee_is_charged_to_sender_and_paid_to_vault(bool jovian, ulong expectedOperatorFee)
    {
        (IWorldState state, IDisposable scope, ITransactionProcessor processor) = CreateOperatorFeeProcessor();
        using IDisposable stateScope = scope;
        Transaction tx = BuildTransfer(processor, jovian);

        TransactionResult result = processor.Execute(tx, NullTxTracer.Instance);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(TransactionResult.Ok));
            Assert.That(state.GetBalance(PreDeploys.OperatorFeeRecipient), Is.EqualTo((UInt256)expectedOperatorFee));
            Assert.That(state.GetBalance(Sender), Is.EqualTo(SenderBalance - tx.Value - GasCostOf.Transaction * TransferGasPrice - expectedOperatorFee));
            Assert.That(TotalBalance(state), Is.EqualTo(SenderBalance));
        }
    }

    [Test]
    public void Operator_fee_is_returned_to_sender_on_call_and_restore([Values] bool jovian)
    {
        (IWorldState state, IDisposable scope, ITransactionProcessor processor) = CreateOperatorFeeProcessor();
        using IDisposable stateScope = scope;
        Transaction tx = BuildTransfer(processor, jovian);

        TransactionResult result = processor.CallAndRestore(tx, NullTxTracer.Instance);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(TransactionResult.Ok));
            Assert.That(state.GetBalance(Sender), Is.EqualTo(SenderBalance));
            Assert.That(TotalBalance(state), Is.EqualTo(SenderBalance));
        }
    }

    [Test]
    public void Sender_balance_must_cover_operator_fee_for_gas_limit([Values] bool jovian, [Values] bool oneWeiShort)
    {
        (IWorldState state, IDisposable scope, ITransactionProcessor processor) = CreateOperatorFeeProcessor();
        using IDisposable stateScope = scope;
        Transaction tx = BuildTransfer(processor, jovian);
        BlockHeader header = BuildHeader(jovian);
        OptimismCostHelper costHelper = new(Spec.Instance, L1Block);
        UInt256 required = tx.Value + (UInt256)TransferGasLimit * TransferGasPrice
            + costHelper.ComputeL1Cost(tx, header, state)
            + costHelper.ComputeOperatorCost(TransferGasLimit, header, state);
        UInt256 balance = oneWeiShort ? required - UInt256.One : required;
        state.SubtractFromBalance(Sender, SenderBalance - balance, OperatorFeeSpecProvider.GenesisSpec);

        TransactionResult result = processor.Execute(tx, NullTxTracer.Instance);

        Assert.That(result, Is.EqualTo(oneWeiShort ? TransactionResult.InsufficientMaxFeePerGasForSenderBalance : TransactionResult.Ok));
    }

    private static ChainSpec CreateChainSpec() => new()
    {
        Genesis = Build.A.Block.Genesis.TestObject,
        Parameters = new ChainParameters(),
        Allocations = [],
        EngineChainSpecParametersProvider = new TestChainSpecParametersProvider(
            new OptimismChainSpecEngineParameters { BedrockBlockNumber = 0, RegolithTimestamp = 0 })
    };

    private static (IWorldState, IDisposable, ITransactionProcessor) CreateOperatorFeeProcessor()
    {
        ChainSpec chainSpec = CreateChainSpec();
        IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(chainSpec))
            .AddModule(new OptimismModule(chainSpec, new OptimismConfig()))
            .AddSingleton<ISpecProvider>(OperatorFeeSpecProvider)
            .AddSingleton<IOptimismSpecHelper>(Spec.Instance)
            .AddSingleton<ICostHelper>(new OptimismCostHelper(Spec.Instance, L1Block))
            .Build();
        ILifetimeScope lifetime = container.BeginLifetimeScope(builder => builder
            .AddSingleton<IWorldStateScopeProvider>(container.Resolve<IWorldStateManager>().GlobalWorldState));
        IWorldState state = lifetime.Resolve<IWorldState>();
        IDisposable scope = state.BeginScope(IWorldState.PreGenesis);
        state.CreateAccount(Sender, SenderBalance);
        state.CreateAccount(PreDeploys.OperatorFeeRecipient, 0, 1);
        state.CreateAccount(L1Block, 0, 1);
        state.Set(new StorageCell(L1Block, 8), new UInt256(OperatorFeeConstant, OperatorFeeScalar));
        state.Commit(OperatorFeeSpecProvider.GenesisSpec);
        state.CommitTree(0);

        return (state, new CompositeDisposable { scope, lifetime, container }, lifetime.Resolve<ITransactionProcessor>());
    }

    private static Transaction BuildTransfer(ITransactionProcessor processor, bool jovian)
    {
        Transaction tx = Build.A.Transaction
            .WithTo(Recipient)
            .WithValue(1)
            .WithGasPrice(TransferGasPrice)
            .WithGasLimit(TransferGasLimit)
            .SignedAndResolved(new EthereumEcdsa(OperatorFeeSpecProvider.ChainId), TestItem.PrivateKeyA).TestObject;

        BlockHeader header = BuildHeader(jovian);
        processor.SetBlockExecutionContext(new BlockExecutionContext(header, OperatorFeeSpecProvider.GetSpec(header)));
        return tx;
    }

    private static BlockHeader BuildHeader(bool jovian) => Build.A.BlockHeader
        .WithNumber(1)
        .WithTimestamp(jovian ? Spec.JovianTimeStamp : Spec.IsthmusTimeStamp)
        .WithBaseFee(1)
        .WithBeneficiary(Beneficiary)
        .WithGasLimit(TransferGasLimit)
        .TestObject;

    private static UInt256 TotalBalance(IWorldState state) =>
        state.GetBalance(Sender) + state.GetBalance(Recipient) + state.GetBalance(Beneficiary)
        + state.GetBalance(BaseFeeRecipient) + state.GetBalance(PreDeploys.OperatorFeeRecipient);

    [Test]
    public void Sender_recipient_and_fee_recipient_balances_are_exact([Values] bool jovian, [Values] bool callAndRestore)
    {
        IReleaseSpec spec = Prague.Instance;
        Address l1Block = TestItem.AddressE;
        using IContainer container = BuildContainer(new OptimismChainSpecEngineParameters
        {
            BedrockBlockNumber = 0,
            RegolithTimestamp = 0,
            IsthmusTimestamp = 0,
            JovianTimestamp = jovian ? 0 : null,
            L1BlockAddress = l1Block,
            L1FeeRecipient = TestItem.AddressF
        }, spec);
        using ILifetimeScope lifetime = BeginLifetimeScope(container);
        IWorldState state = lifetime.Resolve<IWorldState>();
        using IDisposable scope = state.BeginScope(IWorldState.PreGenesis);

        const ulong operatorFeeScalar = 2_000_000;
        const ulong operatorFeeConstant = 1_000;
        const ulong l1BaseFee = 1_000_000_000;
        const ulong l1FeeScalar = 1_000_000;
        UInt256 initialBalance = 1.Ether;
        state.CreateAccount(l1Block, 0);
        state.Set(new StorageCell(l1Block, 1), l1BaseFee);
        state.Set(new StorageCell(l1Block, 6), l1FeeScalar);
        state.Set(new StorageCell(l1Block, 8), (new UInt256(operatorFeeScalar) << 64) + operatorFeeConstant);
        state.CreateAccount(TestItem.AddressA, initialBalance);
        state.CreateAccount(PreDeploys.OperatorFeeRecipient, 1);
        state.CreateAccount(TestItem.AddressF, 1);
        state.Commit(spec);

        const ulong gasPrice = 10;
        UInt256 value = 1_000;
        Transaction tx = Build.A.Transaction
            .WithSenderAddress(TestItem.AddressA)
            .WithTo(TestItem.AddressB)
            .WithValue(value)
            .WithGasPrice(gasPrice)
            .WithGasLimit(100_000)
            .TestObject;
        BlockHeader header = Build.A.BlockHeader.WithBaseFee(0).WithGasLimit(30_000_000).TestObject;

        ITransactionProcessor processor = lifetime.Resolve<ITransactionProcessor>();
        BlockExecutionContext blockContext = new(header, spec);
        TransactionResult result = callAndRestore
            ? processor.CallAndRestore(tx, in blockContext, NullTxTracer.Instance)
            : processor.Execute(tx, in blockContext, NullTxTracer.Instance);

        const ulong gasUsed = GasCostOf.Transaction;
        UInt256 l1Fee = OptimismCostHelper.ComputeL1CostPreEcotone(OptimismCostHelper.ComputeDataGas(tx, isRegolith: true), l1BaseFee, l1FeeScalar);
        UInt256 operatorFee = jovian
            ? (UInt256)gasUsed * operatorFeeScalar * 100 + operatorFeeConstant
            : gasUsed * operatorFeeScalar / 1_000_000 + operatorFeeConstant;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(TransactionResult.Ok));
            Assert.That(l1Fee, Is.Not.EqualTo(UInt256.Zero));
            if (callAndRestore)
            {
                Assert.That(state.GetBalance(TestItem.AddressA), Is.EqualTo(initialBalance));
                Assert.That(state.GetBalance(TestItem.AddressB), Is.EqualTo(UInt256.Zero));
                Assert.That(state.GetBalance(TestItem.AddressF), Is.EqualTo((UInt256)1));
                Assert.That(state.GetBalance(PreDeploys.OperatorFeeRecipient), Is.EqualTo((UInt256)1));
            }
            else
            {
                Assert.That(state.GetBalance(TestItem.AddressA), Is.EqualTo(initialBalance - value - gasUsed * gasPrice - l1Fee - operatorFee));
                Assert.That(state.GetBalance(TestItem.AddressB), Is.EqualTo(value));
                Assert.That(state.GetBalance(TestItem.AddressF), Is.EqualTo(l1Fee + 1));
                Assert.That(state.GetBalance(PreDeploys.OperatorFeeRecipient), Is.EqualTo(operatorFee + 1));
            }
        }
    }

    private static IContainer BuildContainer(OptimismChainSpecEngineParameters parameters, IReleaseSpec spec)
    {
        ChainSpec chainSpec = new()
        {
            Genesis = Build.A.Block.Genesis.TestObject,
            Parameters = new ChainParameters(),
            Allocations = [],
            EngineChainSpecParametersProvider = new TestChainSpecParametersProvider(parameters)
        };
        return new ContainerBuilder()
            .AddModule(new TestNethermindModule(chainSpec))
            .AddModule(new OptimismModule(chainSpec, new OptimismConfig()))
            .AddSingleton<ISpecProvider>(new TestSpecProvider(spec))
            .Build();
    }

    private static ILifetimeScope BeginLifetimeScope(IContainer container) =>
        container.BeginLifetimeScope(builder => builder
            .AddSingleton<IWorldStateScopeProvider>(container.Resolve<IWorldStateManager>().GlobalWorldState));
}
