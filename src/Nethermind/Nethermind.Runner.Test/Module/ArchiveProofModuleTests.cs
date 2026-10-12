// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Core;
using Nethermind.Core.Test.Modules;
using Nethermind.Db;
using Nethermind.State.Flat;
using Nethermind.State.Flat.History;
using Nethermind.State.Flat.History.Proofs;
using NUnit.Framework;

namespace Nethermind.Runner.Test.Module;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public class ArchiveProofModuleTests
{
    [Test]
    public void EveryArchiveProofComponent_SharesTheOneCommitmentMetadata()
    {
        using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(new FlatDbConfig
            {
                Enabled = true,
                HistoryEnabled = true,
                ArchiveProofBuildEnabled = true,
                ArchiveProofServeEnabled = true
            }))
            .Build();

        CommitmentMetadata metadata = container.Resolve<CommitmentMetadata>();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(container.Resolve<ArchiveProofRetrofit>().Metadata, Is.SameAs(metadata),
                "the reclaim turn, the layout flag and the window lock live on the metadata; the walk, the emitters and the reclaimer must share the instance");
            Assert.That(container.Resolve<IHistoricalTrieVisitor>(), Is.TypeOf<ArchiveProofSource>());
            Assert.That(container.Resolve<HistoryWalkVerificationCoordinator>(), Is.Not.Null);
            Assert.That(container.Resolve<CommitmentReclaimer>(), Is.Not.Null);
            Assert.That(container.Resolve<ForwardCommitmentCapture>(), Is.Not.Null);
        }
    }
}
