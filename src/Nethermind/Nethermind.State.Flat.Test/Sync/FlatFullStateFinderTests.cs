// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.State.Flat.Sync;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test.Sync;

[TestFixture]
public class FlatFullStateFinderTests
{
    [Test]
    public void Reports_persisted_state_when_at_or_below_best_header()
    {
        FlatFullStateFinder finder = CreateFinder(persistedBlockNumber: 100, bestHeaderNumber: 150);

        Assert.That(finder.FindBestFullState(), Is.EqualTo(100UL));
    }

    [Test]
    public void Clamps_persisted_state_to_best_header_when_ahead()
    {
        FlatFullStateFinder finder = CreateFinder(persistedBlockNumber: 164, bestHeaderNumber: 100);

        Assert.That(finder.FindBestFullState(), Is.EqualTo(100UL));
    }

    [Test]
    public void Reports_zero_before_genesis_state_is_persisted()
    {
        IPersistenceManager persistenceManager = Substitute.For<IPersistenceManager>();
        persistenceManager.GetCurrentPersistedStateId().Returns(StateId.PreGenesis);

        FlatFullStateFinder finder = new(persistenceManager, Substitute.For<IBlockTree>(), LimboLogs.Instance);

        Assert.That(finder.FindBestFullState(), Is.EqualTo(0UL));
    }

    [Test]
    public void Clamps_to_zero_when_no_header_is_suggested()
    {
        IPersistenceManager persistenceManager = Substitute.For<IPersistenceManager>();
        persistenceManager.GetCurrentPersistedStateId().Returns(new StateId(100, TestItem.KeccakA));

        FlatFullStateFinder finder = new(persistenceManager, Substitute.For<IBlockTree>(), LimboLogs.Instance);

        Assert.That(finder.FindBestFullState(), Is.EqualTo(0UL));
    }

    private static FlatFullStateFinder CreateFinder(ulong persistedBlockNumber, ulong bestHeaderNumber)
    {
        IPersistenceManager persistenceManager = Substitute.For<IPersistenceManager>();
        persistenceManager.GetCurrentPersistedStateId().Returns(new StateId(persistedBlockNumber, TestItem.KeccakA));

        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.BestSuggestedHeader.Returns(Build.A.BlockHeader.WithNumber(bestHeaderNumber).TestObject);

        return new FlatFullStateFinder(persistenceManager, blockTree, LimboLogs.Instance);
    }
}
