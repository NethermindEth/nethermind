// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Blockchain;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Modules;
using Nethermind.Evm.State;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using Nethermind.State;
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

    // EIP-7906: a POST_TX frame reads the transaction's own diff, which exists only if something records
    // it, and eth_call has no block-level recorder. These envs also back mempool admission and the
    // parallel block-access-list parent readers, so a chain that never schedules the fork pays nothing.
    [TestCase(false, TestName = "Create_ForkNeverScheduled_NoDiffRecorder")]
    [TestCase(true, TestName = "Create_ForkScheduled_CarriesAnIdleDiffRecorder")]
    public void Create_DiffRecorderFollowsTheForkSchedule(bool schedulesEip7906)
    {
        ISpecProvider specProvider = new TestSpecProvider(
            new OverridableReleaseSpec(Cancun.Instance) { IsEip7906Enabled = schedulesEip7906, IsEip7928Enabled = schedulesEip7906 });
        using IReadOnlyTxProcessorSource source = new AutoReadOnlyTxProcessingEnvFactory(
            _container.Resolve<ILifetimeScope>(), _container.Resolve<IWorldStateManager>(), specProvider).Create();

        using IReadOnlyTxProcessingScope scope = source.Build(IWorldState.PreGenesis);

        Assert.That(scope.WorldState is IBlockAccessListSource, Is.EqualTo(schedulesEip7906));
        if (schedulesEip7906)
        {
            Assert.That(((IBlockAccessListSource)scope.WorldState).GeneratedBlockAccessList, Is.Null);
        }
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
}
