// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Autofac;
using Autofac.Core;
using Nethermind.Api.Steps;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Core.Exceptions;
using Nethermind.Init.Modules;
using Nethermind.Init.Steps;
using Nethermind.Logging;
using Nethermind.State.Flat.NodeBuffer;
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
    public void Native_trie_node_buffer_is_registered_by_default()
    {
        FlatDbConfig config = new();
        using IContainer container = new ContainerBuilder()
            .AddModule(new FlatWorldStateModule(config))
            .AddSingleton<IFlatDbConfig>(config)
            .AddSingleton<ILogManager>(LimboLogs.Instance)
            .Build();

        Assert.That(container.Resolve<INodeRecordBuffer>(), Is.Not.Null);
    }

    [Test]
    public void Native_backend_validation_runs_on_resolution_not_module_registration([Values] bool enabled)
    {
        FlatDbConfig config = new() { Enabled = enabled, VerifyWithTrie = true };
        using IContainer container = new ContainerBuilder()
            .AddModule(new FlatWorldStateModule(config))
            .AddSingleton<IFlatDbConfig>(config)
            .Build();

        DependencyResolutionException error = Assert.Throws<DependencyResolutionException>(() => container.Resolve<INodeRecordBuffer>())!;
        Exception cause = error.GetBaseException();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cause, Is.TypeOf<InvalidConfigurationException>());
            Assert.That(cause.Message, Does.Contain("VerifyWithTrie"));
        }
    }
}
