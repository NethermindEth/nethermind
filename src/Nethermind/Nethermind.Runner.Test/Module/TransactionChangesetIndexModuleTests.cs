// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using Nethermind.Core;
using Nethermind.Core.Test.Modules;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.DebugModule;
using Nethermind.JsonRpc.Modules.Trace;
using Nethermind.State.Flat.History.Changesets;
using Nethermind.State.OverridableEnv;
using NUnit.Framework;

namespace Nethermind.Runner.Test.Module;

[TestFixture]
public class TransactionChangesetIndexModuleTests
{
    [Test]
    public void ParallelTraceBudget_WhenConfigured_BoundsTheWorkerDegree([Values(-1, 0, 1, 4, 64)] int configured)
    {
        using ParallelTraceBudget budget = new(new FlatDbConfig { HistoryTransactionIndexTraceParallelism = configured });
        int expected = Math.Clamp(configured == 0 ? Environment.ProcessorCount : configured, 1, 16);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(budget.Degree, Is.EqualTo(expected));
            Assert.That(budget.Slots.CurrentCount, Is.EqualTo(expected), "both namespaces must use the same bounded permits");
        }
    }

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

    [TestCase(true, TestName = "WithTheIndexOn_TheTraceEnvironmentCarriesAReadOverlaySlot")]
    [TestCase(false, TestName = "WithTheIndexOff_TheTraceEnvironmentIsUndecorated")]
    public void The_read_overlay_follows_the_index_switch(bool indexEnabled)
    {
        using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(new FlatDbConfig { Enabled = true, HistoryEnabled = true, HistoryTransactionIndexEnabled = indexEnabled }))
            .Build();

        IOverridableEnv env = container.Resolve<IOverridableEnvFactory>().Create();
        using ILifetimeScope scope = container.BeginLifetimeScope(builder => builder.AddModule(env));

        Assert.That(scope.IsRegistered<StateReadOverlaySlot>(), Is.EqualTo(indexEnabled),
            "the slot exists exactly when the scope provider consults it; a slot nothing reads would let the executor skip a prefix no one supplies");
    }

    [Test]
    public void The_debug_and_trace_module_factories_build_with_the_index_on()
    {
        using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(new FlatDbConfig { Enabled = true, HistoryEnabled = true, HistoryTransactionIndexEnabled = true }))
            .Build();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(container.Resolve<DebugModuleFactory>().Create(), Is.Not.Null, "the shared parallel tracer and its environments resolve from the node container");
            Assert.That(container.Resolve<TraceModuleFactory>().Create(), Is.Not.Null);
        }
    }

    [Test]
    public void The_builder_resolves_with_the_index_on()
    {
        using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(new FlatDbConfig { Enabled = true, HistoryEnabled = true, HistoryTransactionIndexEnabled = true, HistoryTransactionIndexWorkers = 2 }))
            .Build();

        TransactionChangesetBuilder builder = container.Resolve<TransactionChangesetBuilder>();
        builder.Dispose();

        Assert.That(container.Dispose, Throws.Nothing, "the container disposes its singletons too; a second dispose must be harmless");
    }
}
