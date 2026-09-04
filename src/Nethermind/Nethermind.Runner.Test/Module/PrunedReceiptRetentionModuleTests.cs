// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
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
                HistoryRetention = HistoryRetentionMode.Rolling, HistoryRetentionBlocks = 1024,
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
