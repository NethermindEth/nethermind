// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Autofac;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
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

        Assert.That(ctx.ChildComponents.WorldState, Is.Not.EqualTo(ctx.WorldStateManager.GlobalWorldState));
        Assert.That(ctx.ChildComponents.StateReader, Is.Not.EqualTo(ctx.WorldStateManager.GlobalStateReader));
        Assert.That(ctx.ChildComponents.CodeInfoRepository, Is.AssignableTo<OverridableCodeInfoRepository>());
        Assert.That(ctx.ChildComponents.TransactionProcessor, Is.AssignableTo<TestTransactionProcessor>());
        Assert.That(((TestTransactionProcessor)ctx.ChildComponents.TransactionProcessor).WorldState, Is.EqualTo(ctx.ChildComponents.WorldState));
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

        Assert.That(ctx.ChildComponents.WorldState.StateRoot, Is.Not.EqualTo(Keccak.EmptyTreeHash));
        Assert.That(scope.Component.WorldState.GetBalance(TestItem.AddressA), Is.EqualTo((UInt256)123));
    }

    // EIP-7906: a POST_TX frame reads the transaction's own diff, so a chain that schedules the fork
    // carries a recorder here. It stays idle - which is what keeps state overrides in the prestate - and
    // the transaction processor must share the very instance the scope hands out, or the opcodes see no diff.
    [TestCase(false, TestName = "BuildAndOverride_ForkNeverScheduled_NoDiffRecorder")]
    [TestCase(true, TestName = "BuildAndOverride_ForkScheduled_CarriesAnIdleDiffRecorderSharedWithTheProcessor")]
    public void BuildAndOverride_DiffRecorderFollowsTheForkSchedule(bool schedulesEip7906)
    {
        using TestContext ctx = new(new TestSpecProvider(
            new OverridableReleaseSpec(Cancun.Instance) { IsEip7906Enabled = schedulesEip7906, IsEip7928Enabled = schedulesEip7906 }));

        using Scope<Components> scope = ctx.Env.BuildAndOverride(
            Build.A.BlockHeader.TestObject,
            new Dictionary<Address, AccountOverride>
            {
                { TestItem.AddressA, new AccountOverride { Balance = 123 } }
            });

        Assert.That(scope.Component.WorldState is IBlockAccessListSource, Is.EqualTo(schedulesEip7906));
        Assert.That(((TestTransactionProcessor)scope.Component.TransactionProcessor).WorldState,
            Is.SameAs(scope.Component.WorldState));
        if (schedulesEip7906)
        {
            Assert.That(((IBlockAccessListSource)scope.Component.WorldState).GeneratedBlockAccessList, Is.Null);
        }
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

        Assert.That(invalidOverride, Throws.TypeOf<ArgumentException>().With.Message.Contains($"Account {TestItem.AddressA} is not a precompile"));

        using Scope<Components> scope = ctx.Env.BuildAndOverride(
            Build.A.BlockHeader.TestObject,
            new Dictionary<Address, AccountOverride>
            {
                { TestItem.AddressA, new AccountOverride { Balance = 456 } }
            });

        Assert.That(scope.Component.WorldState.GetBalance(TestItem.AddressA), Is.EqualTo((UInt256)456));
    }

    private sealed class TestContext : IDisposable
    {
        private readonly IContainer _container;
        private readonly ILifetimeScope _childLifetime;

        public IWorldStateManager WorldStateManager { get; }
        public Components ChildComponents { get; }
        public IOverridableEnv<Components> Env { get; }

        public TestContext(ISpecProvider? specProvider = null)
        {
            _container = new ContainerBuilder()
                .AddModule(new TestNethermindModule())
                .AddScoped<ITransactionProcessor, TestTransactionProcessor>()
                .Add<Components>()
                .Build();

            WorldStateManager = _container.Resolve<IWorldStateManager>();
            ILifetimeScope rootLifetime = _container.Resolve<ILifetimeScope>();
            // A caller-supplied provider drives the fork-schedule gates the factory reads at construction.
            IOverridableEnvFactory envFactory = specProvider is null
                ? _container.Resolve<IOverridableEnvFactory>()
                : new OverridableEnvFactory(WorldStateManager, rootLifetime, specProvider);
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

        public TransactionResult Process(Transaction transaction, ITxTracer txTracer, ExecutionOptions options) =>
            throw new NotImplementedException();

        public void SetBlockExecutionContext(BlockHeader blockHeader) =>
            throw new NotImplementedException();

        public void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext) =>
            throw new NotImplementedException();
    }
}
