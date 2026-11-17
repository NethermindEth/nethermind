// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
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
        ChainSpec chainSpec = new()
        {
            Genesis = Build.A.Block.Genesis.TestObject,
            Parameters = new ChainParameters(),
            Allocations = [],
            EngineChainSpecParametersProvider = new TestChainSpecParametersProvider(
                new OptimismChainSpecEngineParameters { BedrockBlockNumber = 0, RegolithTimestamp = 0 })
        };
        using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(chainSpec))
            .AddModule(new OptimismModule(chainSpec, new OptimismConfig()))
            .AddSingleton<ISpecProvider>(new TestSpecProvider(spec))
            .Build();
        using ILifetimeScope lifetime = container.BeginLifetimeScope(builder => builder
            .AddSingleton<IWorldStateScopeProvider>(container.Resolve<IWorldStateManager>().GlobalWorldState));
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
}
