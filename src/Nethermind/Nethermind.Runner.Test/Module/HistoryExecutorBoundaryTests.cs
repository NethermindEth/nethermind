// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Db;
using Nethermind.Specs.Forks;
using Nethermind.State.Flat.History.Changesets;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Runner.Test.Module;

public class HistoryExecutorBoundaryTests
{
    [Test]
    public void SupportedRange_ReusesBoundaryAndRevalidatesCanonicalHeaders([Values] bool missingHeader)
    {
        Dictionary<ulong, BlockHeader> headers = [];
        for (ulong number = 0; number < 64; number++)
            headers[number] = Build.A.BlockHeader.WithNumber(number).WithTimestamp(number < 32 ? number : number + 100).TestObject;
        int reads = 0;
        IBlockTree tree = Substitute.For<IBlockTree>();
        tree.FindHeader(Arg.Any<ulong>(), BlockTreeLookupOptions.RequireCanonical).Returns(call =>
        {
            reads++;
            return headers.GetValueOrDefault(call.Arg<ulong>());
        });
        ISpecProvider specs = Substitute.For<ISpecProvider>();
        specs.GenesisSpec.Returns(Prague.Instance);
        specs.GetSpec(Arg.Any<ForkActivation>()).Returns(call =>
            call.Arg<ForkActivation>().Timestamp >= 100 ? Amsterdam.Instance : Prague.Instance);
        using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(new FlatDbConfig { HistoryEnabled = true, HistoryTransactionIndexEnabled = true }))
            .AddSingleton(tree)
            .AddSingleton(specs)
            .Build();
        IHistoryBlockExecutorFactory factory = container.Resolve<IHistoryBlockExecutorFactory>();

        Assert.That(factory.GetLastSupportedBlock(0, 63), Is.EqualTo(31));
        reads = 0;
        Assert.That(factory.GetLastSupportedBlock(0, 63), Is.EqualTo(31));
        Assert.That(reads, Is.LessThanOrEqualTo(4), "a stable fork boundary must not trigger another binary search");
        BlockHeader first = headers[0];
        headers.Remove(0);
        Assert.That(factory.GetLastSupportedBlock(0, 63), Is.Null, "cached boundaries cannot bypass a missing canonical lower bound");
        headers[0] = first;
        Assert.That(factory.GetLastSupportedBlock(32, 63), Is.Null);
        Assert.That(factory.GetLastSupportedBlock(0, 20), Is.EqualTo(20));

        if (missingHeader) headers.Remove(31);
        else headers[31] = Build.A.BlockHeader.WithNumber(31).WithTimestamp(131).TestObject;
        Assert.That(factory.GetLastSupportedBlock(0, 63), Is.EqualTo(missingHeader ? null : (ulong?)30),
            "a missing or replaced canonical boundary must invalidate the cached range");
    }
}
