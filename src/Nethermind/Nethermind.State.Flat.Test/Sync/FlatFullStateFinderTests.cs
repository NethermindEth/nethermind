// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Test.Builders;
using Nethermind.State.Flat.Sync;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test.Sync;

[TestFixture]
public class FlatFullStateFinderTests
{
    [Test]
    public void FindBestFullState_WhenStatePersisted_ReturnsPersistedBlockNumber()
    {
        FlatFullStateFinder finder = CreateFinder(new StateId(164, TestItem.KeccakA));

        Assert.That(finder.FindBestFullState(), Is.EqualTo(164UL));
    }

    [Test]
    public void FindBestFullState_BeforeGenesisStatePersisted_ReturnsZero()
    {
        FlatFullStateFinder finder = CreateFinder(StateId.PreGenesis);

        Assert.That(finder.FindBestFullState(), Is.EqualTo(0UL));
    }

    private static FlatFullStateFinder CreateFinder(StateId persisted)
    {
        IPersistenceManager persistenceManager = Substitute.For<IPersistenceManager>();
        persistenceManager.GetCurrentPersistedStateId().Returns(persisted);

        return new FlatFullStateFinder(persistenceManager);
    }
}
