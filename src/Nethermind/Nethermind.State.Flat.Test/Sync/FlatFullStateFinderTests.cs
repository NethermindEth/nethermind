// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.State.Flat.Sync;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test.Sync;

[TestFixture]
public class FlatFullStateFinderTests
{
    [TestCase(100ul, 150ul, 100ul)]
    [TestCase(164ul, 100ul, 100ul)]
    [TestCase(100ul, null, 0ul)]
    public void FindBestFullState_WhenStatePersisted_ClampsToBestSuggestedHeader(ulong persistedNumber, ulong? bestHeaderNumber, ulong expected)
    {
        FlatFullStateFinder finder = CreateFinder(new StateId(persistedNumber, TestItem.KeccakA), bestHeaderNumber);

        Assert.That(finder.FindBestFullState(), Is.EqualTo(expected));
    }

    [Test]
    public void FindBestFullState_BeforeGenesisStatePersisted_ReturnsZero()
    {
        FlatFullStateFinder finder = CreateFinder(StateId.PreGenesis, bestHeaderNumber: 100);

        Assert.That(finder.FindBestFullState(), Is.EqualTo(0UL));
    }

    private static FlatFullStateFinder CreateFinder(StateId persisted, ulong? bestHeaderNumber)
    {
        IPersistenceManager persistenceManager = Substitute.For<IPersistenceManager>();
        persistenceManager.GetCurrentPersistedStateId().Returns(persisted);

        IBlockTree blockTree = Substitute.For<IBlockTree>();
        if (bestHeaderNumber is not null)
        {
            blockTree.BestSuggestedHeader.Returns(Build.A.BlockHeader.WithNumber(bestHeaderNumber.Value).TestObject);
        }

        return new FlatFullStateFinder(persistenceManager, blockTree, LimboLogs.Instance);
    }
}
