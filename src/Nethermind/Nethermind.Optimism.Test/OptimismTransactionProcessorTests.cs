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
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test.ChainSpecStyle;
using Nethermind.State;
using NUnit.Framework;

namespace Nethermind.Optimism.Test;

public class OptimismTransactionProcessorTests
{
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

    [Test]
    public void System_flagged_deposit_after_Regolith_is_rejected_without_state_changes([Values(0UL, 500_000_000_000_000_000UL)] ulong value)
    {
        using SystemDepositRun run = ExecuteSystemDeposit(regolithTimestamp: 0, value, gasLimit: 100_000);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(run.Result.Error, Is.EqualTo(TransactionResult.ErrorType.MalformedTransaction));
            Assert.That(run.State.AccountExists(TestItem.AddressA), Is.False);
            Assert.That(run.State.GetBalance(TestItem.AddressB), Is.EqualTo(UInt256.Zero));
            Assert.That(run.Header.GasUsed, Is.Zero);
        }
    }

    [Test]
    public void System_flagged_deposit_before_Regolith_mints_and_increments_nonce([Values(0UL, 500_000_000_000_000_000UL)] ulong value)
    {
        using SystemDepositRun run = ExecuteSystemDeposit(regolithTimestamp: null, value, gasLimit: 100_000);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(run.Result, Is.EqualTo(TransactionResult.Ok));
            Assert.That(run.Tracer.StatusCode, Is.EqualTo(StatusCode.Success));
            Assert.That(run.State.GetBalance(TestItem.AddressA), Is.EqualTo(1.Ether - value));
            Assert.That(run.State.GetNonce(TestItem.AddressA), Is.EqualTo(1UL));
            Assert.That(run.State.GetBalance(TestItem.AddressB), Is.EqualTo((UInt256)value));
            Assert.That(run.Header.GasUsed, Is.Zero);
        }
    }

    [Test]
    public void System_flagged_deposit_before_Regolith_is_not_limited_by_block_gas()
    {
        using SystemDepositRun run = ExecuteSystemDeposit(regolithTimestamp: null, value: 0, gasLimit: 150_000_000);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(run.Result, Is.EqualTo(TransactionResult.Ok));
            Assert.That(run.Tracer.StatusCode, Is.EqualTo(StatusCode.Success));
            Assert.That(run.State.GetNonce(TestItem.AddressA), Is.EqualTo(1UL));
            Assert.That(run.Header.GasUsed, Is.Zero);
        }
    }

    [Test]
    public void Failed_system_flagged_deposit_before_Regolith_uses_no_block_gas()
    {
        using SystemDepositRun run = ExecuteSystemDeposit(regolithTimestamp: null, value: 0, gasLimit: 1_000);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(run.Result, Is.EqualTo(TransactionResult.Ok));
            Assert.That(run.Tracer.StatusCode, Is.EqualTo(StatusCode.Failure));
            Assert.That(run.Tracer.GasSpent, Is.Zero);
            Assert.That(run.Header.GasUsed, Is.Zero);
            Assert.That(run.State.GetBalance(TestItem.AddressA), Is.EqualTo(1.Ether));
            Assert.That(run.State.GetNonce(TestItem.AddressA), Is.EqualTo(1UL));
        }
    }

    private static SystemDepositRun ExecuteSystemDeposit(ulong? regolithTimestamp, ulong value, ulong gasLimit)
    {
        IReleaseSpec spec = Prague.Instance;
        IContainer container = BuildContainer(new OptimismChainSpecEngineParameters { BedrockBlockNumber = 0, RegolithTimestamp = regolithTimestamp }, spec);
        ILifetimeScope lifetime = BeginLifetimeScope(container);
        IWorldState state = lifetime.Resolve<IWorldState>();
        IDisposable scope = state.BeginScope(IWorldState.PreGenesis);

        Transaction tx = Build.A.Transaction
            .WithType(TxType.DepositTx)
            .WithSourceHash(TestItem.KeccakA)
            .WithSenderAddress(TestItem.AddressA)
            .WithTo(TestItem.AddressB)
            .WithValue(value)
            .WithGasLimit(gasLimit)
            .WithIsOPSystemTransaction(true)
            .TestObject;
        tx.Mint = 1.Ether;
        BlockHeader header = Build.A.BlockHeader.WithGasLimit(30_000_000).TestObject;
        CallOutputTracer tracer = new();

        TransactionResult result = lifetime.Resolve<ITransactionProcessor>()
            .Execute(tx, new BlockExecutionContext(header, spec), tracer);
        return new SystemDepositRun(container, lifetime, scope, state, header, tracer, result);
    }

    private sealed record SystemDepositRun(
        IContainer Container,
        ILifetimeScope Lifetime,
        IDisposable Scope,
        IWorldState State,
        BlockHeader Header,
        CallOutputTracer Tracer,
        TransactionResult Result) : IDisposable
    {
        public void Dispose()
        {
            Scope.Dispose();
            Lifetime.Dispose();
            Container.Dispose();
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
