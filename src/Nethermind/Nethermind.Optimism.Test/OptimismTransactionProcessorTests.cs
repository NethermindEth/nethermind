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
    public void Sender_recipient_and_operator_fee_recipient_balances_are_exact([Values] bool jovian)
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
        UInt256 initialBalance = 1.Ether;
        state.CreateAccount(l1Block, 0);
        state.Set(new StorageCell(l1Block, 8), (new UInt256(operatorFeeScalar) << 64) + operatorFeeConstant);
        state.CreateAccount(TestItem.AddressA, initialBalance);
        state.CreateAccount(PreDeploys.OperatorFeeRecipient, 1);
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

        TransactionResult result = lifetime.Resolve<ITransactionProcessor>()
            .Execute(tx, new BlockExecutionContext(header, spec), NullTxTracer.Instance);

        const ulong gasUsed = GasCostOf.Transaction;
        UInt256 operatorFee = jovian
            ? (UInt256)gasUsed * operatorFeeScalar * 100 + operatorFeeConstant
            : gasUsed * operatorFeeScalar / 1_000_000 + operatorFeeConstant;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(TransactionResult.Ok));
            Assert.That(state.GetBalance(TestItem.AddressA), Is.EqualTo(initialBalance - value - gasUsed * gasPrice - operatorFee));
            Assert.That(state.GetBalance(TestItem.AddressB), Is.EqualTo(value));
            Assert.That(state.GetBalance(PreDeploys.OperatorFeeRecipient), Is.EqualTo(operatorFee + 1));
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
