// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Autofac;
using Nethermind.Api.Steps;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.Db;
using Nethermind.Init.Modules;
using Nethermind.Init.Steps;
using Nethermind.Logging;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.Rust;
using Nethermind.State.Flat.Rust.Interop;
using NUnit.Framework;
using NUnit.Framework.Constraints;

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

    [TestCase(FlatDbBackend.CSharp, true, true, false, null)]
    [TestCase(FlatDbBackend.RustPersistence, true, false, false, null, Description = "Fails fast without the native library")]
    [TestCase(FlatDbBackend.Rust, true, false, false, "not implemented yet")]
    [TestCase(FlatDbBackend.Rust, true, true, false, "supports neither")]
    [TestCase(FlatDbBackend.RustPersistence, true, false, true, "supports neither")]
    [TestCase(FlatDbBackend.Rust, false, true, false, null, Description = "Flat disabled")]
    public void Rust_backends_fail_fast_on_unsupported_configurations(FlatDbBackend backend, bool enabled, bool longFinality, bool history, string error)
    {
        FlatDbConfig config = new() { Backend = backend, Enabled = enabled, EnableLongFinality = longFinality, HistoryEnabled = history };
        if (error is null && enabled && backend == FlatDbBackend.RustPersistence && !NativeMethods.IsAvailable) error = "libnethermind_flatdb.so";

        IResolveConstraint outcome = error is null ? Throws.Nothing : Throws.TypeOf<InvalidConfigurationException>().With.Message.Contains(error);

        Assert.That(() => new ContainerBuilder().AddModule(new FlatWorldStateModule(config)).Build().Dispose(), outcome);
    }

    [Test]
    public void RustPersistence_backend_replaces_the_persistence()
    {
        if (!NativeMethods.IsAvailable) Assert.Ignore("libnethermind_flatdb.so not built");
        FlatDbConfig config = new() { Backend = FlatDbBackend.RustPersistence, EnableLongFinality = false };
        using IContainer container = new ContainerBuilder()
            .AddModule(new FlatWorldStateModule(config))
            .AddSingleton<IFlatDbConfig>(config)
            .AddSingleton<ILogManager>(LimboLogs.Instance)
            .AddSingleton<IColumnsDb<FlatDbColumns>>(new SnapshotableMemColumnsDb<FlatDbColumns>())
            .Build();

        Assert.That(container.Resolve<IPersistence>(), Is.InstanceOf<RustPersistence>());
    }
}
