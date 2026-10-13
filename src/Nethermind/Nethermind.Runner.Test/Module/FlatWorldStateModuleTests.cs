// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Api;
using Nethermind.Api.Steps;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Test.IO;
using Nethermind.Db;
using Nethermind.Init.Modules;
using Nethermind.Init.Steps;
using Nethermind.Logging;
using Nethermind.Monitoring.Config;
using Nethermind.State;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.Rust;
using Nethermind.State.Flat.Rust.Interop;
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.State.Flat.Sync.Snap;
using NSubstitute;
using NUnit.Framework;
using NUnit.Framework.Constraints;
using Fork = Nethermind.State.Flat.Rust.Fork;

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
    [TestCase(FlatDbBackend.Rust, true, false, false, null, Description = "Fails fast without the native library")]
    [TestCase(FlatDbBackend.Rust, true, true, false, "supports neither")]
    [TestCase(FlatDbBackend.RustPersistence, true, false, true, "supports neither")]
    [TestCase(FlatDbBackend.Rust, false, true, false, null, Description = "Flat disabled")]
    public void Rust_backends_fail_fast_on_unsupported_configurations(FlatDbBackend backend, bool enabled, bool longFinality, bool history, string error)
    {
        FlatDbConfig config = new() { Backend = backend, Enabled = enabled, EnableLongFinality = longFinality, HistoryEnabled = history };
        if (error is null && enabled && backend != FlatDbBackend.CSharp && !NativeMethods.IsAvailable) error = "libnethermind_flatdb.so";

        IResolveConstraint outcome = error is null ? Throws.Nothing : Throws.TypeOf<InvalidConfigurationException>().With.Message.Contains(error);

        Assert.That(() => new ContainerBuilder().AddModule(new FlatWorldStateModule(config)).Build().Dispose(), outcome);
    }

    [TestCase(FlatDbBackend.RustPersistence, typeof(FlatDbManager), typeof(PersistenceManager), 0)]
    [TestCase(FlatDbBackend.Rust, typeof(FlatDbManagerGuard), typeof(RustPersistenceManager), 0)]
    [TestCase(FlatDbBackend.Rust, typeof(FlatDbManagerGuard), typeof(RustPersistenceManager), 2)]
    public async Task Rust_backends_replace_their_components(FlatDbBackend backend, Type flatDbManager, Type persistenceManager, int trieWarmerWorkers)
    {
        if (!NativeMethods.IsAvailable) Assert.Ignore("libnethermind_flatdb.so not built");
        FlatDbConfig config = new() { Backend = backend, EnableLongFinality = false, TrieWarmerWorkerCount = trieWarmerWorkers };
        using TempPath basePath = TempPath.GetTempDirectory();
        await using IContainer container = new ContainerBuilder()
            .AddModule(new FlatWorldStateModule(config))
            .AddSingleton<IFlatDbConfig>(config)
            .AddSingleton<ILogManager>(LimboLogs.Instance)
            .AddSingleton<IColumnsDb<FlatDbColumns>>(new SnapshotableMemColumnsDb<FlatDbColumns>())
            .AddKeyedSingleton<IDb>(DbNames.Metadata, new MemDb())
            .AddKeyedSingleton<IDb>(DbNames.Code, new MemDb())
            .AddSingleton<IBlocksConfig>(new BlocksConfig())
            .AddSingleton<IInitConfig>(new InitConfig { BaseDbPath = basePath.Path })
            .AddSingleton<IMetricsConfig>(new MetricsConfig())
            .AddSingleton<IProcessExitSource>(new ProcessExitSource(CancellationToken.None))
            .AddSingleton<IStateHeaderProvider>(UnavailableStateHeaderProvider.Instance)
            .AddSingleton<IStatePersistenceBarrier>(NullStatePersistenceBarrier.Instance)
            .AddSingleton(Substitute.For<IFlatStateRootIndex>())
            .Build();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(container.Resolve<IPersistence>(), Is.InstanceOf<RustPersistence>());
            Assert.That(container.Resolve<IPersistenceManager>(), Is.InstanceOf(persistenceManager));
            Assert.That(container.Resolve<IFlatDbManager>(), Is.InstanceOf(flatDbManager));
            if (backend == FlatDbBackend.Rust)
            {
                IFlatDbManager guard = container.Resolve<IFlatDbManager>();
                Assert.That(container.Resolve<Fork.IFlatDbManager>(), Is.SameAs(container.Resolve<Fork.RustFlatDbManager>()));
                Assert.That(container.Resolve<Fork.FlatWorldStateManager>().GlobalStateReader, Is.InstanceOf<Fork.FlatStateReader>());
                Assert.That(guard.HasStateForBlock(StateId.PreGenesis), Is.EqualTo(container.Resolve<Fork.IFlatDbManager>().HasStateForBlock(StateId.PreGenesis)));
                Assert.That(() => guard.GatherReadOnlySnapshotBundle(StateId.PreGenesis), Throws.TypeOf<NotSupportedException>());
                Assert.That(() => guard.GatherSnapshotBundle(StateId.PreGenesis, ResourcePool.Usage.ReadOnlyProcessingEnv), Throws.TypeOf<NotSupportedException>());
                Assert.That(container.Resolve<ITrieWarmer>(), trieWarmerWorkers > 0 ? Is.InstanceOf<Fork.TrieWarmer>() : Is.InstanceOf<NoopTrieWarmer>());
            }
        }
    }
}
