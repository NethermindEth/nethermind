// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Evm.TransactionProcessing;
using NUnit.Framework;

namespace Nethermind.Consensus.Test;

public class ShareableTxProcessingSourceTests
{
    [Test]
    public void OnSubsequentBuild_GiveDifferentWorldState()
    {
        using IContainer container = new ContainerBuilder().AddModule(new TestNethermindModule()).Build();
        IShareableTxProcessorSource shareableSource = container.Resolve<IShareableTxProcessorSource>();

        var scope1 = shareableSource.Build(Build.A.BlockHeader.TestObject);
        var scope2 = shareableSource.Build(Build.A.BlockHeader.TestObject);

        scope1.WorldState.Should().NotBeSameAs(scope2.WorldState);
    }

    [Test]
    public void OnSubsequentBuild_AfterFirstScopeDispose_GiveSameWorldState()
    {
        using IContainer container = new ContainerBuilder().AddModule(new TestNethermindModule()).Build();
        IShareableTxProcessorSource shareableSource = container.Resolve<IShareableTxProcessorSource>();

        var scope1 = shareableSource.Build(Build.A.BlockHeader.TestObject);
        scope1.Dispose();
        var scope2 = shareableSource.Build(Build.A.BlockHeader.TestObject);

        scope1.WorldState.Should().BeSameAs(scope2.WorldState);
    }
}
