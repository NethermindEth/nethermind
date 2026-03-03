// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Autofac;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.State;
using Nethermind.State.OverridableEnv;
using NUnit.Framework;

namespace Nethermind.Evm.Test.OverridableEnv;

[Parallelizable(ParallelScope.All)]
public class DisposableScopeOverridableEnvTests
{
    [Test]
    public void Create_ReturnsEnvWithOverriddenComponents()
    {
        using TestContext ctx = new();

        ctx.ChildComponents.WorldState.Should().NotBe(ctx.WorldStateManager.GlobalWorldState);
        ctx.ChildComponents.StateReader.Should().NotBe(ctx.WorldStateManager.GlobalStateReader);
        ctx.ChildComponents.CodeInfoRepository.Should().BeAssignableTo<OverridableCodeInfoRepository>();
        ctx.ChildComponents.TransactionProcessor.Should().BeAssignableTo<TestTransactionProcessor>();
        ((TestTransactionProcessor)ctx.ChildComponents.TransactionProcessor).WorldState.Should().Be(ctx.ChildComponents.WorldState);
    }

    [Test]
    public void BuildAndOverride_WithBalanceOverride_AppliesStateCorrectly()
    {
        using TestContext ctx = new();

        using Scope<Components> scope = ctx.Env.BuildAndOverride(
            Build.A.BlockHeader.TestObject,
            new Dictionary<Address, AccountOverride>
            {
                { TestItem.AddressA, new AccountOverride { Balance = 123 } }
            });

        ctx.ChildComponents.WorldState.StateRoot.Should().NotBe(Keccak.EmptyTreeHash);
        scope.Component.WorldState.GetBalance(TestItem.AddressA).Should().Be(123);
    }

    [Test]
    public void BuildAndOverride_AfterExceptionFromInvalidStateOverride_CanBeCalledAgain()
    {
        using TestContext ctx = new();

        Action invalidOverride = () => ctx.Env.BuildAndOverride(
            Build.A.BlockHeader.TestObject,
            new Dictionary<Address, AccountOverride>
            {
                { TestItem.AddressA, new AccountOverride { MovePrecompileToAddress = TestItem.AddressB } }
            });

        invalidOverride.Should().Throw<ArgumentException>()
            .WithMessage($"Account {TestItem.AddressA} is not a precompile");

        using Scope<Components> scope = ctx.Env.BuildAndOverride(
            Build.A.BlockHeader.TestObject,
            new Dictionary<Address, AccountOverride>
            {
                { TestItem.AddressA, new AccountOverride { Balance = 456 } }
            });

        scope.Component.WorldState.GetBalance(TestItem.AddressA).Should().Be(456);
    }

    private sealed class TestContext : IDisposable
    {
        private readonly IContainer _container;
        private readonly ILifetimeScope _childLifetime;

        public IWorldStateManager WorldStateManager { get; }
        public Components ChildComponents { get; }
        public IOverridableEnv<Components> Env { get; }

        public TestContext()
        {
            _container = new ContainerBuilder()
                .AddModule(new TestNethermindModule())
                .AddScoped<ITransactionProcessor, TestTransactionProcessor>()
                .Add<Components>()
                .Build();

            WorldStateManager = _container.Resolve<IWorldStateManager>();
            IOverridableEnvFactory envFactory = _container.Resolve<IOverridableEnvFactory>();
            ILifetimeScope rootLifetime = _container.Resolve<ILifetimeScope>();
            IOverridableEnv envModule = envFactory.Create();

            _childLifetime = rootLifetime.BeginLifetimeScope(builder => builder.AddModule(envModule));
            ChildComponents = _childLifetime.Resolve<Components>();
            Env = _childLifetime.Resolve<IOverridableEnv<Components>>();
        }

        public void Dispose()
        {
            _childLifetime.Dispose();
            _container.Dispose();
        }
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

        public TransactionResult Execute(Transaction transaction, ITxTracer txTracer) =>
            throw new NotImplementedException();

        public TransactionResult CallAndRestore(Transaction transaction, ITxTracer txTracer) =>
            throw new NotImplementedException();

        public TransactionResult BuildUp(Transaction transaction, ITxTracer txTracer) =>
            throw new NotImplementedException();

        public TransactionResult Trace(Transaction transaction, ITxTracer txTracer) =>
            throw new NotImplementedException();

        public TransactionResult Warmup(Transaction transaction, ITxTracer txTracer) =>
            throw new NotImplementedException();

        public void SetBlockExecutionContext(BlockHeader blockHeader) =>
            throw new NotImplementedException();

        public void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext) =>
            throw new NotImplementedException();
    }
}
