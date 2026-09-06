// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
    [Test]
    public void WithFlatHistory_TheArchiveProofSourceWinsOverTheNullDefault()
    {
        using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(new FlatDbConfig { Enabled = true, HistoryEnabled = true }))
            .Build();

        Assert.That(container.Resolve<IHistoricalTrieVisitor>(), Is.TypeOf<ArchiveProofSource>(),
            "the flat-history module registers the proof source after the world-state module's null default, which must therefore be registered to lose");
    }

    [Test]
    public void WithoutFlatHistory_TheNullDefaultServesNothing()
    {
        using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(new FlatDbConfig { Enabled = true, HistoryEnabled = false }))
            .Build();

        Assert.That(container.Resolve<IHistoricalTrieVisitor>(), Is.TypeOf<NullHistoricalTrieVisitor>());
    }
}
