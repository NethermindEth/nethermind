// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using Nethermind.Core;
using Nethermind.Core.Test.Modules;
using Nethermind.Db;
using Nethermind.Evm.Tracing;
using Nethermind.State.Flat.History.Changesets;
using NUnit.Framework;

namespace Nethermind.Runner.Test.Module;

[TestFixture]
public class TransactionChangesetIndexModuleTests
{
    [TestCase(true, typeof(ChangesetPrefixStateSeedSource), TestName = "WithFlatHistory_TheChangesetSeedSourceWinsOverTheNullDefault")]
    [TestCase(false, typeof(NullPrefixStateSeedSource), TestName = "WithoutFlatHistory_TheNullDefaultKeepsTheReplay")]
    public void The_prefix_seed_source_follows_the_flat_history_switch(bool historyEnabled, Type expected)
    {
        using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(new FlatDbConfig { Enabled = true, HistoryEnabled = historyEnabled }))
            .Build();

        Assert.That(container.Resolve<IPrefixStateSeedSource>(), Is.TypeOf(expected));
    }

    [Test]
    public void An_executor_can_be_built_from_the_node_container()
    {
        using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(new FlatDbConfig { Enabled = true, HistoryEnabled = true, HistoryTransactionIndexEnabled = true }))
            .Build();

        using IHistoryBlockExecutor executor = container.Resolve<IHistoryBlockExecutorFactory>().Create();

        Assert.That(executor, Is.Not.Null, "the executor scope must carry everything a block replay resolves, or the builder thread dies on its first block");
    }

    [Test]
    public void The_builder_resolves_with_the_index_on()
    {
        using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(new FlatDbConfig { Enabled = true, HistoryEnabled = true, HistoryTransactionIndexEnabled = true, HistoryTransactionIndexWorkers = 2 }))
            .Build();

        using TransactionChangesetBuilder builder = container.Resolve<TransactionChangesetBuilder>();

        Assert.That(builder, Is.Not.Null);
    }
}
