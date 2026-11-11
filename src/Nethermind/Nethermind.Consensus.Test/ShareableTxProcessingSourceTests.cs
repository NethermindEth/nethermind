// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Blockchain;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Test.Modules;
using Nethermind.Evm.State;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using Nethermind.State;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Consensus.Test;

public class ShareableTxProcessingSourceTests
{
    private IContainer _container;
    private IShareableTxProcessorSource _shareableSource;

    [SetUp]
    public void Setup()
    {
        _container = new ContainerBuilder().AddModule(new TestNethermindModule()).Build();
        _shareableSource = _container.Resolve<IShareableTxProcessorSource>();
    }

    [TearDown]
    public void TearDown()
    {
        _shareableSource?.Dispose();
        _container?.Dispose();
    }

    // The processor records the EIP-7906 ASSERT diff itself, so eth_call, mempool admission and the parallel
    // BAL parent readers all run on the plain world state. Checked on a chain that schedules the fork, where a
    // scope-level recorder used to be wired in.
    [Test]
    public void Create_CarriesNoDiffRecorder()
    {
        using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(new OverridableReleaseSpec(Cancun.Instance) { IsEip7906Enabled = true, IsEip7928Enabled = true }))
            .Build();
        using IReadOnlyTxProcessorSource source = new AutoReadOnlyTxProcessingEnvFactory(
            container.Resolve<ILifetimeScope>(), container.Resolve<IWorldStateManager>()).Create();

        using IReadOnlyTxProcessingScope scope = source.Build(IWorldState.PreGenesis);

        Assert.That(scope.WorldState, Is.Not.InstanceOf<IBlockAccessListSource>());
    }

    // Only the shareable source serves read-only queries; block processing helpers, the prewarmer, block production
    // and the BAL parent readers keep creating their envs from the plain factory.
    [TestCase(true, TestName = "Build_ShareableSource_UsesReadOnlyQueryWorldState")]
    [TestCase(false, TestName = "Create_ReadOnlyTxProcessingEnvFactory_UsesResettableWorldState")]
    public void Build_WorldStateFollowsTheSource(bool shareable)
    {
        IWorldStateManager spy = null!;
        using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule())
            .AddDecorator<IWorldStateManager>((_, inner) => spy = ForwardingSpy(inner))
            .Build();

        if (shareable)
        {
            using IShareableTxProcessorSource source = container.Resolve<IShareableTxProcessorSource>();
            source.Build(IWorldState.PreGenesis).Dispose();
        }
        else
        {
            using IReadOnlyTxProcessorSource source = container.Resolve<IReadOnlyTxProcessingEnvFactory>().Create();
            source.Build(IWorldState.PreGenesis).Dispose();
        }

        using (Assert.EnterMultipleScope())
        {
            spy.Received(shareable ? 1 : 0).CreateReadOnlyQueryWorldState();
            spy.Received(shareable ? 0 : 1).CreateResettableWorldState();
        }
    }

    [Test]
    public void Build_FactoryDecoratorWithoutQueryOverload_StillCreatesTheEnvs()
    {
        LegacyFactoryDecorator decorator = null!;
        using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule())
            .AddDecorator<IReadOnlyTxProcessingEnvFactory>((_, inner) => decorator = new LegacyFactoryDecorator(inner))
            .Build();

        using IShareableTxProcessorSource source = container.Resolve<IShareableTxProcessorSource>();
        source.Build(IWorldState.PreGenesis).Dispose();

        Assert.That(decorator.CreateCalls, Is.EqualTo(1), "the shareable source must reach a registered factory decorator");
    }

    [Test]
    public void OnSubsequentBuild_GiveDifferentWorldState()
    {
        IReadOnlyTxProcessingScope scope1 = _shareableSource.Build(IWorldState.PreGenesis);
        IReadOnlyTxProcessingScope scope2 = _shareableSource.Build(IWorldState.PreGenesis);

        Assert.That(scope1.WorldState, Is.Not.SameAs(scope2.WorldState));
    }

    [Test]
    public void OnSubsequentBuild_AfterFirstScopeDispose_GiveSameWorldState()
    {
        IReadOnlyTxProcessingScope scope1 = _shareableSource.Build(IWorldState.PreGenesis);
        scope1.Dispose();
        IReadOnlyTxProcessingScope scope2 = _shareableSource.Build(IWorldState.PreGenesis);

        Assert.That(scope1.WorldState, Is.SameAs(scope2.WorldState));
    }

    private static IWorldStateManager ForwardingSpy(IWorldStateManager inner)
    {
        IWorldStateManager spy = Substitute.For<IWorldStateManager>();
        spy.GlobalWorldState.Returns(inner.GlobalWorldState);
        spy.GlobalStateReader.Returns(inner.GlobalStateReader);
        spy.CreateResettableWorldState().Returns(_ => inner.CreateResettableWorldState());
        spy.CreateReadOnlyQueryWorldState().Returns(_ => inner.CreateReadOnlyQueryWorldState());
        return spy;
    }

    private sealed class LegacyFactoryDecorator(IReadOnlyTxProcessingEnvFactory inner) : IReadOnlyTxProcessingEnvFactory
    {
        public int CreateCalls { get; private set; }

        public IReadOnlyTxProcessorSource Create()
        {
            CreateCalls++;
            return inner.Create();
        }
    }
}
