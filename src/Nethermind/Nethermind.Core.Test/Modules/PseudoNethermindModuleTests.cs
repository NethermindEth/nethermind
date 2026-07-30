// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.Receipts;
using Nethermind.Config;
using Nethermind.Core.Exceptions;
using Nethermind.Db.LogIndex;
using Nethermind.Init.Modules;
using Nethermind.Db;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.PersistedSnapshots.Storage;
using NUnit.Framework;

namespace Nethermind.Core.Test.Modules;

public class PseudoNethermindModuleTests
{
    // Regeneration re-executes a block, so it must stay unreachable from everything that is not a read-only query:
    // peer-facing serving, and consensus components that read receipts while processing (AuRa validator contract,
    // Shutter). Those resolve the unkeyed registration, which must therefore never become the regenerating one.
    [TestCase(true)]
    [TestCase(false)]
    public void Default_receipt_finder_is_never_the_regenerating_one(bool deriveFromState)
    {
        using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(
                new ReceiptConfig { DeriveFromState = deriveFromState },
                new FlatDbConfig { Enabled = true, HistoryEnabled = true }))
            .Build();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(container.Resolve<IReceiptFinder>(), Is.InstanceOf<FullInfoReceiptFinder>());
            Assert.That(container.ResolveKeyed<IReceiptFinder>(IReceiptFinder.RegenerableKey),
                deriveFromState ? Is.InstanceOf<RegeneratingReceiptFinder>() : Is.InstanceOf<FullInfoReceiptFinder>());
        }
    }

    // The guard against permanent receipt loss: bodies skipped under a config that cannot reproduce them are gone
    // for good, so the wrong combinations must refuse to start rather than warn.
    [TestCase(false, false, true, TestName = "DeriveFromState_refuses_to_start_without_flat_history")]
    [TestCase(true, true, true, TestName = "DeriveFromState_refuses_to_start_with_log_index")]
    [TestCase(true, false, false, TestName = "DeriveFromState_accepts_flat_history_without_log_index")]
    public void DeriveFromState_startup_validation(bool historyEnabled, bool logIndexEnabled, bool expectRefusal)
    {
        ConfigProvider configProvider = new(
            new ReceiptConfig { DeriveFromState = true },
            new FlatDbConfig { Enabled = historyEnabled, HistoryEnabled = historyEnabled },
            new LogIndexConfig { Enabled = logIndexEnabled });

        void Validate() => NethermindModule.ValidateReceiptDerivationConfig(configProvider);

        if (expectRefusal)
            Assert.Throws<InvalidConfigurationException>(Validate);
        else
            Assert.DoesNotThrow(Validate);
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
