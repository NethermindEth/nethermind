// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Autofac;
using Nethermind.Api.Steps;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Init.Modules;
using Nethermind.Init.Steps;
using Nethermind.Logging;
using Nethermind.State.Flat;
using NUnit.Framework;

namespace Nethermind.Runner.Test.Module;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public class FlatWorldStateModuleTests
{
    [Test]
    public void Drop_step_is_registered_before_InitializeBlockchain_only_when_flagged([Values] bool drop)
    {
        // The dependent edge is what keeps the deletion inside init rather than under block processing.
        using IContainer container = new ContainerBuilder()
            .AddModule(new FlatWorldStateModule(new FlatDbConfig { DropPruningTrieState = drop }))
            .Build();

        StepInfo step = container.Resolve<IEnumerable<StepInfo>>().SingleOrDefault(s => s.StepBaseType == typeof(DropPruningTrieState));

        Assert.That(step is not null, Is.EqualTo(drop));
        if (drop)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(step.Dependencies, Does.Contain(typeof(InitializeBlockTree)));
                Assert.That(step.Dependents, Does.Contain(typeof(InitializeBlockchain)));
            }
        }
    }

    [Test]
    public void Managed_trie_node_cache_is_replaced_by_noop_with_native_bulk_set([Values] bool useNativeBulkSet)
    {
        FlatDbConfig config = new() { UseNativeBulkSet = useNativeBulkSet, TrieCacheMemoryBudget = 1024 * 1024 };
        using IContainer container = new ContainerBuilder()
            .AddModule(new FlatWorldStateModule(config))
            .AddSingleton<IFlatDbConfig>(config)
            .AddSingleton<ILogManager>(LimboLogs.Instance)
            .Build();

        Assert.That(container.Resolve<ITrieNodeCache>(), useNativeBulkSet ? Is.TypeOf<NoopTrieNodeCache>() : Is.TypeOf<TrieNodeCache>());
    }
}
