// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.Forks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.Receipts;
using Nethermind.Config;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Test.Builders;
using Nethermind.Db.LogIndex;
using Nethermind.Init.Modules;
using Nethermind.Db;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.PersistedSnapshots.Storage;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Core.Test.Modules;

public class PseudoNethermindModuleTests
{
    [Test]
    public void Default_backend_follows_suite_selection([Range(0, 3)] int constructor)
    {
        bool expectedFlatDb = Environment.GetEnvironmentVariable(TestStateBackend.UseTrieEnvironmentVariable) != "1";
        TestNethermindModule module = constructor switch
        {
            0 => new TestNethermindModule(Osaka.Instance),
            1 => new TestNethermindModule(Array.Empty<IConfig>()),
            2 => new TestNethermindModule(new ChainSpec()),
            _ => TestNethermindModule.CreateWithRealChainSpec()
        };
        using IContainer container = new ContainerBuilder().AddModule(module).Build();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(TestStateBackend.UseFlatDb, Is.EqualTo(expectedFlatDb));
            Assert.That(container.Resolve<IFlatDbConfig>().Enabled, Is.EqualTo(expectedFlatDb));
        }
    }

    [Test]
    public void Explicit_backend_is_preserved([Values] bool enabled, [Values] bool historyEnabled, [Values] bool useProvider)
    {
        FlatDbConfig flatDbConfig = new() { Enabled = enabled, HistoryEnabled = historyEnabled };
        TestNethermindModule module = useProvider
            ? new TestNethermindModule(new ConfigProvider(flatDbConfig))
            : new TestNethermindModule(flatDbConfig);
        using IContainer container = new ContainerBuilder().AddModule(module).Build();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(container.Resolve<IFlatDbConfig>().Enabled, Is.EqualTo(enabled));
            Assert.That(container.Resolve<IFlatDbConfig>().HistoryEnabled, Is.EqualTo(historyEnabled));
        }
    }

    [Test]
    public async Task Test_blockchain_preserves_explicit_backend([Values] bool enabled, [Values] bool historyEnabled)
    {
        using BackendTestBlockchain chain = new(new FlatDbConfig { Enabled = enabled, HistoryEnabled = historyEnabled });
        chain.UseFlatDb = !enabled;
        await chain.Initialize();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(chain.Container.Resolve<IFlatDbConfig>().Enabled, Is.EqualTo(enabled));
            Assert.That(chain.Container.Resolve<IFlatDbConfig>().HistoryEnabled, Is.EqualTo(historyEnabled));
        }
    }

    [Test]
    public async Task Test_blockchain_applies_backend_property([Values] bool enabled)
    {
        using BackendTestBlockchain chain = new(null);
        Assert.That(chain.UseFlatDb, Is.EqualTo(TestStateBackend.UseFlatDb));
        chain.UseFlatDb = enabled;
        await chain.Initialize();

        Assert.That(chain.Container.Resolve<IFlatDbConfig>().Enabled, Is.EqualTo(enabled));
    }

    private sealed class BackendTestBlockchain(FlatDbConfig? flatDbConfig) : BasicTestBlockchain
    {
        public Task<TestBlockchain> Initialize() => Build();

        protected override IEnumerable<IConfig> CreateConfigs() => flatDbConfig is null
            ? base.CreateConfigs()
            : [.. base.CreateConfigs(), flatDbConfig];
    }

    // Regeneration re-executes a block, so it must stay unreachable from everything that is not a read-only query:
    // peer-facing serving, and consensus components that read receipts while processing (AuRa validator contract,
    // Shutter). Those resolve the unkeyed registration, which must therefore never become the regenerating one.
    [Test]
    public void Default_receipt_finder_is_never_the_regenerating_one([Values] bool deriveFromState)
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

    [Test]
    public void Regenerating_finder_wraps_the_unkeyed_finder()
    {
        IReceiptFinder pluginFinder = Substitute.For<IReceiptFinder>();
        Hash256 marker = TestItem.KeccakB;
        pluginFinder.FindBlockHash(TestItem.KeccakA).Returns(marker);

        using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(
                new ReceiptConfig { DeriveFromState = true },
                new FlatDbConfig { Enabled = true, HistoryEnabled = true }))
            .AddSingleton<IReceiptFinder>(pluginFinder)
            .Build();

        IReceiptFinder regenerable = container.ResolveKeyed<IReceiptFinder>(IReceiptFinder.RegenerableKey);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(regenerable, Is.InstanceOf<RegeneratingReceiptFinder>());
            Assert.That(regenerable.FindBlockHash(TestItem.KeccakA), Is.EqualTo(marker),
                "the plugin's finder must serve the regenerable path");
        }
    }

    // The guard against permanent receipt loss: bodies skipped under a config that cannot reproduce them are gone
    // for good, so the wrong combinations must refuse to start rather than warn.
    [TestCase(false, false, true, true, TestName = "DeriveFromState_refuses_to_start_without_flat_history")]
    [TestCase(true, true, true, true, TestName = "DeriveFromState_refuses_to_start_with_log_index")]
    [TestCase(true, false, false, true, TestName = "DeriveFromState_refuses_to_start_without_receipt_storage")]
    [TestCase(true, false, true, false, TestName = "DeriveFromState_accepts_flat_history_without_log_index")]
    public void DeriveFromState_startup_validation(bool historyEnabled, bool logIndexEnabled, bool storeReceipts, bool expectRefusal)
    {
        ConfigProvider configProvider = new(
            new ReceiptConfig { DeriveFromState = true, StoreReceipts = storeReceipts },
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
