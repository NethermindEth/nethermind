// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using Nethermind.Core;
using Nethermind.Core.Test.Modules;
using Nethermind.Db;
using Nethermind.State.Flat;
using Nethermind.State.Flat.History.Proofs;
using NUnit.Framework;

namespace Nethermind.Runner.Test.Module;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public class HistoricalTrieVisitorModuleTests
{
    [TestCase(true, typeof(ArchiveProofSource), TestName = "WithFlatHistory_TheArchiveProofSourceWinsOverTheNullDefault")]
    [TestCase(false, typeof(NullHistoricalTrieVisitor), TestName = "WithoutFlatHistory_TheNullDefaultServesNothing")]
    public void The_historical_visitor_follows_the_flat_history_switch(bool historyEnabled, Type expected)
    {
        using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(new FlatDbConfig { Enabled = true, HistoryEnabled = historyEnabled }))
            .Build();

        Assert.That(container.Resolve<IHistoricalTrieVisitor>(), Is.TypeOf(expected),
            "the flat-history module registers the proof source after the world-state module's null default, which must therefore be registered to lose");
    }
}
