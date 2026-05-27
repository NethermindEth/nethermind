// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Synchronization;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.Sync.Snap;
using Nethermind.State.Snap;
using Nethermind.Synchronization.SnapSync;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test.Sync.Snap;

[TestFixture]
public class FlatSnapTrieFactoryTests
{
    private static (FlatSnapTrieFactory factory, IPersistence persistence) Build(bool doubleWriteCheck = false)
    {
        IPersistence persistence = Substitute.For<IPersistence>();
        persistence.CreateReader(Arg.Any<ReaderFlags>()).Returns(_ => Substitute.For<IPersistence.IPersistenceReader>());
        persistence.CreateWriteBatch(Arg.Any<StateId>(), Arg.Any<StateId>(), Arg.Any<Nethermind.Core.WriteFlags>())
            .Returns(_ => Substitute.For<IPersistence.IWriteBatch>());

        ISyncConfig syncConfig = Substitute.For<ISyncConfig>();
        syncConfig.EnableSnapDoubleWriteCheck.Returns(doubleWriteCheck);

        FlatSnapTrieFactory factory = new(persistence, syncConfig, LimboLogs.Instance);
        return (factory, persistence);
    }

    [Test]
    public void EnsureInitialize_ClearsDatabase()
    {
        (FlatSnapTrieFactory factory, IPersistence persistence) = Build();

        factory.EnsureInitialize();

        persistence.Received(1).Clear();
    }

    [Test]
    public void FinalizeSync_FlushesPersistence()
    {
        (FlatSnapTrieFactory factory, IPersistence persistence) = Build();

        factory.FinalizeSync();

        persistence.Received(1).Flush();
    }

    [Test]
    public void CreateTrees_DoNotClearDatabase()
    {
        (FlatSnapTrieFactory factory, IPersistence persistence) = Build();

        using (ISnapTree<PathWithAccount> stateTree = factory.CreateStateTree())
        using (ISnapTree<PathWithStorageSlot> storageTree = factory.CreateStorageTree(default))
        using (ISnapTree<PathWithStorageSlot> nonDefaultStorageTree = factory.CreateStorageTree(new ValueHash256(Bytes.FromHexString("11" + new string('0', 62)))))
        {
            Assert.That(stateTree, Is.Not.Null);
            Assert.That(storageTree, Is.Not.Null);
            Assert.That(nonDefaultStorageTree, Is.Not.Null);
        }

        // Clear is the runner's responsibility via EnsureInitialize; tree creation must not invoke it.
        persistence.DidNotReceive().Clear();
    }

    [TestCase(true)]
    [TestCase(false)]
    public void Factory_CreatesTreesWithoutThrowing_ForBothDoubleWriteFlagValues(bool doubleWriteCheck)
    {
        (FlatSnapTrieFactory factory, _) = Build(doubleWriteCheck);

        using ISnapTree<PathWithAccount> stateTree = factory.CreateStateTree();
        using ISnapTree<PathWithStorageSlot> storageTree = factory.CreateStorageTree(default);

        Assert.That(stateTree, Is.Not.Null);
        Assert.That(storageTree, Is.Not.Null);
    }
}
