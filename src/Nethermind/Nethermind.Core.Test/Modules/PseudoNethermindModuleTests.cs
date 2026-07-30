// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Db;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.PersistedSnapshots.Storage;
using NUnit.Framework;

namespace Nethermind.Core.Test.Modules;

public class PseudoNethermindModuleTests
{
    [Test]
    public void FlatDb_test_container_wires_inert_persisted_snapshot_tier()
    {
        using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(new FlatDbConfig { Enabled = true }))
            .Build();

        Assert.That(container.Resolve<ISnapshotCatalog>(), Is.SameAs(NullSnapshotCatalog.Instance));
        Assert.That(container.Resolve<IPersistedSnapshotLoader>(), Is.SameAs(NullPersistedSnapshotLoader.Instance));
        Assert.That(container.Resolve<IPersistedSnapshotCompactor>(), Is.SameAs(NullPersistedSnapshotCompactor.Instance));
    }
}
