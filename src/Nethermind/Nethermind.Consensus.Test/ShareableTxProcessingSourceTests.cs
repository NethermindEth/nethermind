// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Test.Modules;
using Nethermind.Evm.State;
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

    [Test]
    public void OnSubsequentBuild_GiveDifferentWorldState()
    {
        IReadOnlyTxProcessingScope scope1 = _shareableSource.Build(IWorldState.PreGenesis);
        IReadOnlyTxProcessingScope scope2 = _shareableSource.Build(IWorldState.PreGenesis);

        scope1.WorldState.Should().NotBeSameAs(scope2.WorldState);
    }

    [Test]
    public void OnSubsequentBuild_AfterFirstScopeDispose_GiveSameWorldState()
    {
        IReadOnlyTxProcessingScope scope1 = _shareableSource.Build(IWorldState.PreGenesis);
        scope1.Dispose();
        IReadOnlyTxProcessingScope scope2 = _shareableSource.Build(IWorldState.PreGenesis);

        scope1.WorldState.Should().BeSameAs(scope2.WorldState);
    }
}
