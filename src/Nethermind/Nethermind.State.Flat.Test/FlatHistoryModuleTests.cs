// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Init.Modules;
using Nethermind.Logging;
using Nethermind.Monitoring.Config;
using Nethermind.State.Flat.History;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

public class FlatHistoryModuleTests
{
    [Test]
    public void Decorates_flat_db_manager_and_registers_capture_hook()
    {
        using SnapshotableMemColumnsDb<FlatDbColumns> columns = new();
        using SnapshotableMemColumnsDb<FlatHistoryColumns> historyColumns = new();
        IFlatDbManager inner = Substitute.For<IFlatDbManager>();

        using IContainer container = new ContainerBuilder()
            .AddModule(new FlatHistoryModule())
            .AddSingleton<IFlatDbManager>(_ => inner)
            .AddSingleton<IColumnsDb<FlatDbColumns>>(columns)
            .AddSingleton<IColumnsDb<FlatHistoryColumns>>(historyColumns)
            .AddSingleton<IFlatDbConfig>(new FlatDbConfig { HistoryEnabled = true })
            .AddSingleton<ILogManager>(LimboLogs.Instance)
            .AddSingleton<IPersistenceManager>(_ => Substitute.For<IPersistenceManager>())
            .AddSingleton<ITrieNodeCache>(_ => Substitute.For<ITrieNodeCache>())
            .AddSingleton<IResourcePool>(_ => Substitute.For<IResourcePool>())
            .AddSingleton<IMetricsConfig>(_ => Substitute.For<IMetricsConfig>())
            .Build();

        using (Assert.EnterMultipleScope())
        {
            // Historical reads are served by the decorator wrapping the underlying manager.
            Assert.That(container.Resolve<IFlatDbManager>(), Is.InstanceOf<HistoricalFlatDbManager>());
            // The PersistenceManager's optional capture hook resolves to the history writer.
            Assert.That(container.Resolve<IFlatPersistenceCaptureHook>(), Is.InstanceOf<HistoryWriter>());
        }
    }
}
