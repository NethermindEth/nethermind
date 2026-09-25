// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.Sync.Snap;
using Nethermind.State.Snap;
using Nethermind.Synchronization.SnapSync;
using Nethermind.Trie;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test.Sync.Snap;

[TestFixture]
public class FlatSnapTrieFactoryTests
{
    private static (FlatSnapTrieFactory factory, IPersistence persistence, IPersistence.IPersistenceReader reader) Build(bool doubleWriteCheck = false)
    {
        IPersistence persistence = Substitute.For<IPersistence>();
        IPersistence.IPersistenceReader reader = Substitute.For<IPersistence.IPersistenceReader>();
        persistence.CreateReader(Arg.Any<ReaderFlags>()).Returns(reader);
        // NSubstitute proxies fail with InvalidProgramException on the ReadOnlySpan parameters,
        // so the write batch is a hand-rolled no-op fake.
        persistence.CreateWriteBatch(Arg.Any<StateId>(), Arg.Any<StateId>(), Arg.Any<Nethermind.Core.WriteFlags>())
            .Returns(_ => new NoOpWriteBatch());

        ISyncConfig syncConfig = Substitute.For<ISyncConfig>();
        syncConfig.EnableSnapDoubleWriteCheck.Returns(doubleWriteCheck);

        FlatSnapTrieFactory factory = new(persistence, syncConfig, LimboLogs.Instance);
        return (factory, persistence, reader);
    }

    private class NoOpWriteBatch : IPersistence.IWriteBatch
    {
        public void SelfDestruct(Address addr) { }
        public void SetAccount(Address addr, Account? account) { }
        public void SetStorage(Address addr, in UInt256 slot, in UInt256? value) { }
        public void SetStateTrieNode(in TreePath path, scoped ReadOnlySpan<byte> rlp) { }
        public void SetStorageTrieNode(Hash256 address, in TreePath path, scoped ReadOnlySpan<byte> rlp) { }
        public void SetStorageRawEncoded(in ValueHash256 addrHash, in ValueHash256 slotHash, scoped ReadOnlySpan<byte> rlpValue) { }
        public void SetAccountRaw(in ValueHash256 addrHash, Account account) { }
        public void DeleteAccountRange(in ValueHash256 fromPath, in ValueHash256 toPath) { }
        public void DeleteStorageRange(in ValueHash256 addressHash, in ValueHash256 fromPath, in ValueHash256 toPath) { }
        public void DeleteStateTrieNodeRange(in ValueHash256 from, in ValueHash256 to) { }
        public void DeleteStorageTrieNodeRange(in ValueHash256 addressHash, in ValueHash256 from, in ValueHash256 to) { }
        public void Dispose() { }
    }

    [Test]
    public void EnsureInitialize_ClearsDatabase()
    {
        (FlatSnapTrieFactory factory, IPersistence persistence, _) = Build();

        factory.EnsureInitialize();

        persistence.Received(1).Clear();
    }

    [Test]
    public void FinalizeSync_FlushesPersistence()
    {
        (FlatSnapTrieFactory factory, IPersistence persistence, _) = Build();

        factory.FinalizeSync();

        persistence.Received(1).Flush();
    }

    [Test]
    public void CreateTrees_DoNotClearDatabase()
    {
        (FlatSnapTrieFactory factory, IPersistence persistence, _) = Build();

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

    [Test]
    public void Factory_CreatesTreesWithoutThrowing_ForBothDoubleWriteFlagValues([Values] bool doubleWriteCheck)
    {
        (FlatSnapTrieFactory factory, _, _) = Build(doubleWriteCheck);

        using ISnapTree<PathWithAccount> stateTree = factory.CreateStateTree();
        using ISnapTree<PathWithStorageSlot> storageTree = factory.CreateStorageTree(default);

        Assert.That(stateTree, Is.Not.Null);
        Assert.That(storageTree, Is.Not.Null);
    }

    [Test]
    public void ProoflessRange_DoesNotCreateReader()
    {
        (FlatSnapTrieFactory factory, IPersistence persistence, _) = Build();

        using (ISnapTree<PathWithStorageSlot> storageTree = factory.CreateStorageTree(default))
        {
            // Proofless flow: no boundary stitching, no IsPersisted, double-write check off.
            storageTree.BulkSetAndUpdateRootHash([new PathWithStorageSlot(new ValueHash256("0x1000000000000000000000000000000000000000000000000000000000000000"), [1])]);
            storageTree.Commit(Keccak.MaxValue);
        }

        using (ISnapTree<PathWithAccount> stateTree = factory.CreateStateTree())
        {
        }

        // Creating a reader takes a DB snapshot per account per storage response — it must stay lazy.
        persistence.DidNotReceive().CreateReader(Arg.Any<ReaderFlags>());
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Reader_IsCreatedOnlyOnUse_AndCannotBeUsedAfterDisposal(bool readBeforeDisposal)
    {
        (FlatSnapTrieFactory factory, IPersistence persistence, IPersistence.IPersistenceReader reader) = Build();

        ISnapTree<PathWithStorageSlot> storageTree = factory.CreateStorageTree(default);
        TreePath path = TreePath.Empty;
        if (readBeforeDisposal)
        {
            storageTree.IsPersisted(path, Keccak.EmptyTreeHash.ValueHash256);
            storageTree.IsPersisted(path, Keccak.EmptyTreeHash.ValueHash256);
        }

        storageTree.Dispose();
        storageTree.Dispose();

        Assert.Throws<ObjectDisposedException>(() => storageTree.IsPersisted(path, Keccak.EmptyTreeHash.ValueHash256));
        persistence.Received(readBeforeDisposal ? 1 : 0).CreateReader(ReaderFlags.Sync);
        reader.Received(readBeforeDisposal ? 1 : 0).Dispose();
    }
}
