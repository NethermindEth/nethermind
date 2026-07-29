// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.Receipts;
using Nethermind.Db;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.PersistedSnapshots.Storage;
using NUnit.Framework;

namespace Nethermind.Core.Test.Modules;

public class PseudoNethermindModuleTests
{
    // Regeneration re-executes a block, so it must stay unreachable from peer-facing serving, which any peer can
    // drive. The keyed registration is what keeps it out of the decorator chain.
    [TestCase(true)]
    [TestCase(false)]
    public void Peer_facing_receipt_finder_is_never_the_regenerating_one(bool deriveFromState)
    {
        using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(
                new ReceiptConfig { DeriveFromState = deriveFromState },
                new FlatDbConfig { Enabled = true, HistoryEnabled = true }))
            .Build();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(container.Resolve<IReceiptFinder>(),
                deriveFromState ? Is.InstanceOf<RegeneratingReceiptFinder>() : Is.InstanceOf<FullInfoReceiptFinder>());
            Assert.That(container.ResolveKeyed<IReceiptFinder>(FullInfoReceiptFinder.StoredOnlyKey),
                Is.InstanceOf<FullInfoReceiptFinder>());
        }
    }

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
