// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Autofac;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Evm.OverridableEnv;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.State;
using NUnit.Framework;

namespace Nethermind.Evm.Test.OverridableEnv;

public class OverridableEnvTests
{
    [Test]
    public void TestCreate()
    {
        using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule())
            .AddScoped<ITransactionProcessor, TestTransactionProcessor>()
            .Add<Components>()
            .Build();

        IOverridableEnvFactory envFactory = container.Resolve<IOverridableEnvFactory>();
        IWorldStateManager worldStateManager = container.Resolve<IWorldStateManager>();
        ILifetimeScope rootLifetime = container.Resolve<ILifetimeScope>();
        IOverridableEnv env = envFactory.Create();

        using ILifetimeScope childLifetime = rootLifetime.BeginLifetimeScope(builder => builder.AddModule(env));

        Components childComponents = childLifetime.Resolve<Components>();
        childComponents.WorldState.Should().NotBe(worldStateManager.GlobalWorldState);
        childComponents.StateReader.Should().NotBe(worldStateManager.GlobalStateReader);
        childComponents.CodeInfoRepository.Should().BeAssignableTo<OverridableCodeInfoRepository>();
        childComponents.TransactionProcessor.Should().BeAssignableTo<TestTransactionProcessor>();
        ((TestTransactionProcessor)childComponents.TransactionProcessor).WorldState.Should().Be(childComponents.WorldState);
    }

    [Test]
    public void TestOverriddenState()
    {
        using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule())
            .AddScoped<ITransactionProcessor, TestTransactionProcessor>()
            .Add<Components>()
            .Build();

        IOverridableEnvFactory envFactory = container.Resolve<IOverridableEnvFactory>();
        ILifetimeScope rootLifetime = container.Resolve<ILifetimeScope>();
        IOverridableEnv envModule = envFactory.Create();
        using ILifetimeScope childLifetime = rootLifetime.BeginLifetimeScope(builder => builder.AddModule(envModule));

        Components childComponents = childLifetime.Resolve<Components>();
        IOverridableEnv<Components> env = childLifetime.Resolve<IOverridableEnv<Components>>();

        {
            childComponents.WorldState.StateRoot.Should().Be(Keccak.EmptyTreeHash);
            using var _ = env.BuildAndOverride(Build.A.BlockHeader.TestObject,
                new Dictionary<Address, AccountOverride>()
                {
                    {
                        TestItem.AddressA, new AccountOverride()
                        {
                            Balance = 123
                        }
                    }
                }, out Components component);

            childComponents.WorldState.StateRoot.Should().NotBe(Keccak.EmptyTreeHash);
            component.WorldState.GetBalance(TestItem.AddressA).Should().Be(123);
        }

        childComponents.WorldState.StateRoot.Should().Be(Keccak.EmptyTreeHash);
        childComponents.WorldState.GetBalance(TestItem.AddressA).Should().NotBe(123);
    }

    private record Components(
        IWorldState WorldState,
        ICodeInfoRepository CodeInfoRepository,
        IStateReader StateReader,
        ITransactionProcessor TransactionProcessor
    );

    private class TestTransactionProcessor(IWorldState worldState) : ITransactionProcessor
    {
        public IWorldState WorldState => worldState;

        public TransactionResult Execute(Transaction transaction, ITxTracer txTracer)
        {
            throw new System.NotImplementedException();
        }

        public TransactionResult Execute(Transaction transaction, BlockHeader header, ITxTracer txTracer)
        {
            throw new System.NotImplementedException();
        }

        public TransactionResult CallAndRestore(Transaction transaction, ITxTracer txTracer)
        {
            throw new System.NotImplementedException();
        }

        public TransactionResult CallAndRestore(Transaction transaction, BlockHeader header, ITxTracer txTracer)
        {
            throw new System.NotImplementedException();
        }

        public TransactionResult BuildUp(Transaction transaction, ITxTracer txTracer)
        {
            throw new System.NotImplementedException();
        }

        public TransactionResult Trace(Transaction transaction, ITxTracer txTracer)
        {
            throw new System.NotImplementedException();
        }

        public TransactionResult Warmup(Transaction transaction, ITxTracer txTracer)
        {
            throw new System.NotImplementedException();
        }

        public void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext)
        {
            throw new System.NotImplementedException();
        }
    }
}
