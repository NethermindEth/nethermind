// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Db;
using Nethermind.History;
using NUnit.Framework;

namespace Nethermind.Runner.Test.Module;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public class PrunedReceiptRetentionModuleTests
{
    [Test]
    public void Pruner_uses_the_configured_receipt_store([Values] bool storeReceipts)
    {
        using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(new ReceiptConfig { StoreReceipts = storeReceipts }))
            .Build();

        IReceiptStorage storage = container.Resolve<IReceiptStorage>();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(storage, storeReceipts ? Is.TypeOf<PersistentReceiptStorage>() : Is.SameAs(NullReceiptStorage.Instance));
            Assert.That(container.Resolve<IReceiptPruner>(), Is.SameAs(storage));
        }
    }

    [Test]
    public void Pruner_follows_a_replacement_receipt_store_unless_overridden([Values] bool overridePruner)
    {
        InMemoryReceiptStorage replacement = new();
        ContainerBuilder builder = new();
        if (overridePruner) builder.AddSingleton<IReceiptPruner>(NullReceiptStorage.Instance);
        using IContainer container = builder
            .AddModule(new TestNethermindModule())
            .AddSingleton<IReceiptStorage>(replacement)
            .Build();

        IReceiptPruner expected = overridePruner ? NullReceiptStorage.Instance : replacement;
        Assert.That(container.Resolve<IReceiptPruner>(), Is.SameAs(expected));
    }

    // The config has to arrive through the provider rather than an Intercept: FlatWorldStateModule reads it in its
    // constructor to decide whether to load FlatHistoryModule at all, long before any decorator runs.
    [Test]
    public void ConfiguredSlices_BindTheSlicedPolicy_NotTheDefaultThatNeverRetains()
    {
        using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(new FlatDbConfig
            {
                Enabled = true,
                HistoryEnabled = true,
                HistoryRetention = HistoryRetentionMode.Rolling,
                HistoryRetentionBlocks = 1024,
                HistorySliceAddresses = TestItem.AddressA.ToString()
            }))
            .Build();

        Assert.That(container.Resolve<IPrunedReceiptRetention>(), Is.TypeOf<SlicedReceiptRetention>(),
            "the flat-history module registers this after BlockTreeModule has already run, so a plain default there would win on Autofac's last-registration-wins and silently disable slice receipt retention");
    }

    [Test]
    public void WithoutFlatHistory_TheDefaultNeverRetains()
    {
        using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(new FlatDbConfig { Enabled = false }))
            .Build();

        Assert.That(container.Resolve<IPrunedReceiptRetention>(), Is.TypeOf<NullPrunedReceiptRetention>());
    }
}
