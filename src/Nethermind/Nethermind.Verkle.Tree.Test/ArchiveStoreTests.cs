// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Verkle;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Verkle.Tree.History.V2;
using Nethermind.Verkle.Tree.TreeStore;

namespace Nethermind.Verkle.Tree.Test;

public class ArchiveStoreTests
{
    [TearDown]
    public void CleanTestData()
    {
        string dbPath = VerkleTestUtils.GetDbPathForTest();
        if (Directory.Exists(dbPath))
        {
            Directory.Delete(dbPath, true);
        }
    }

    public static IEnumerable MultiBlockTest
    {
        get
        {
            ulong maxBlock = 100;
            int maxChunk = 20;
            for (ulong i = 10; i < maxBlock; i = i + 10)
            {
                for (int j = 1; j < maxChunk; j++)
                {
                    yield return new TestCaseData(DbMode.MemDb, i, j);
                    yield return new TestCaseData(DbMode.PersistantDb, i, j);
                }
            }
        }
    }

    [TestCase(DbMode.MemDb)]
    [TestCase(DbMode.PersistantDb)]
    public void TestInsertGetMultiBlockReverseStateWithEliasFano(DbMode dbMode)
    {

        IDbProvider provider;
        switch (dbMode)
        {
            case DbMode.MemDb:
                provider = VerkleDbFactory.InitDatabase(dbMode, null);
                break;
            case DbMode.PersistantDb:
                provider = VerkleDbFactory.InitDatabase(dbMode, VerkleTestUtils.GetDbPathForTest());
                break;
            case DbMode.ReadOnlyDb:
            default:
                throw new ArgumentOutOfRangeException(nameof(dbMode), dbMode, null);
        }

        var treeStore = new VerkleTreeStore<PersistEveryBlock>(provider, LimboLogs.Instance);
        var tree = new VerkleTree(treeStore, LimboLogs.Instance);

        VerkleArchiveStore archiveStore = new VerkleArchiveStore(treeStore, provider, LimboLogs.Instance);

        tree.Insert((Hash256)VerkleTestUtils.KeyVersion, VerkleTestUtils.EmptyArray);
        tree.Insert((Hash256)VerkleTestUtils.KeyBalance, VerkleTestUtils.EmptyArray);
        tree.Insert((Hash256)VerkleTestUtils.KeyNonce, VerkleTestUtils.EmptyArray);
        tree.Insert((Hash256)VerkleTestUtils.KeyCodeCommitment, VerkleTestUtils.ValueEmptyCodeHashValue);
        tree.Insert((Hash256)VerkleTestUtils.KeyCodeSize, VerkleTestUtils.EmptyArray);
        tree.Commit();
        tree.CommitTree(0);
        Hash256 stateRoot0 = tree.StateRoot;
        Console.WriteLine(tree.StateRoot.ToString());

        tree.Get(VerkleTestUtils.KeyVersion).Should().BeEquivalentTo(VerkleTestUtils.EmptyArray);
        tree.Get(VerkleTestUtils.KeyBalance).Should().BeEquivalentTo(VerkleTestUtils.EmptyArray);
        tree.Get(VerkleTestUtils.KeyNonce).Should().BeEquivalentTo(VerkleTestUtils.EmptyArray);
        tree.Get(VerkleTestUtils.KeyCodeCommitment).Should().BeEquivalentTo(VerkleTestUtils.ValueEmptyCodeHashValue);
        tree.Get(VerkleTestUtils.KeyCodeSize).Should().BeEquivalentTo(VerkleTestUtils.EmptyArray);

        tree.Insert((Hash256)VerkleTestUtils.KeyVersion, VerkleTestUtils.ArrayAll0Last2);
        tree.Insert((Hash256)VerkleTestUtils.KeyBalance, VerkleTestUtils.ArrayAll0Last2);
        tree.Insert((Hash256)VerkleTestUtils.KeyNonce, VerkleTestUtils.ArrayAll0Last2);
        tree.Insert((Hash256)VerkleTestUtils.KeyCodeCommitment, VerkleTestUtils.ValueEmptyCodeHashValue);
        tree.Insert((Hash256)VerkleTestUtils.KeyCodeSize, VerkleTestUtils.ArrayAll0Last2);
        tree.Commit();
        tree.CommitTree(1);
        Hash256 stateRoot1 = tree.StateRoot;
        Console.WriteLine(tree.StateRoot.ToString());

        tree.Get(VerkleTestUtils.KeyVersion).Should().BeEquivalentTo(VerkleTestUtils.ArrayAll0Last2);
        tree.Get(VerkleTestUtils.KeyBalance).Should().BeEquivalentTo(VerkleTestUtils.ArrayAll0Last2);
        tree.Get(VerkleTestUtils.KeyNonce).Should().BeEquivalentTo(VerkleTestUtils.ArrayAll0Last2);
        tree.Get(VerkleTestUtils.KeyCodeCommitment).Should().BeEquivalentTo(VerkleTestUtils.ValueEmptyCodeHashValue);
        tree.Get(VerkleTestUtils.KeyCodeSize).Should().BeEquivalentTo(VerkleTestUtils.ArrayAll0Last2);

        tree.Insert((Hash256)VerkleTestUtils.KeyVersion, VerkleTestUtils.ArrayAll0Last3);
        tree.Insert((Hash256)VerkleTestUtils.KeyBalance, VerkleTestUtils.ArrayAll0Last3);
        tree.Insert((Hash256)VerkleTestUtils.KeyNonce, VerkleTestUtils.ArrayAll0Last3);
        tree.Insert((Hash256)VerkleTestUtils.KeyCodeCommitment, VerkleTestUtils.ValueEmptyCodeHashValue);
        tree.Insert((Hash256)VerkleTestUtils.KeyCodeSize, VerkleTestUtils.ArrayAll0Last3);
        tree.Commit();
        tree.CommitTree(2);
        Console.WriteLine(tree.StateRoot.ToString());

        tree.Get(VerkleTestUtils.KeyVersion).Should().BeEquivalentTo(VerkleTestUtils.ArrayAll0Last3);
        tree.Get(VerkleTestUtils.KeyBalance).Should().BeEquivalentTo(VerkleTestUtils.ArrayAll0Last3);
        tree.Get(VerkleTestUtils.KeyNonce).Should().BeEquivalentTo(VerkleTestUtils.ArrayAll0Last3);
        tree.Get(VerkleTestUtils.KeyCodeCommitment).Should().BeEquivalentTo(VerkleTestUtils.ValueEmptyCodeHashValue);
        tree.Get(VerkleTestUtils.KeyCodeSize).Should().BeEquivalentTo(VerkleTestUtils.ArrayAll0Last3);

        archiveStore.GetLeaf(VerkleTestUtils.KeyVersion.BytesToArray(), stateRoot1).Should().BeEquivalentTo(VerkleTestUtils.ArrayAll0Last2);
        archiveStore.GetLeaf(VerkleTestUtils.KeyBalance.BytesToArray(), stateRoot1).Should().BeEquivalentTo(VerkleTestUtils.ArrayAll0Last2);
        archiveStore.GetLeaf(VerkleTestUtils.KeyNonce.BytesToArray(), stateRoot1).Should().BeEquivalentTo(VerkleTestUtils.ArrayAll0Last2);
        archiveStore.GetLeaf(VerkleTestUtils.KeyCodeCommitment.BytesToArray(), stateRoot1).Should().BeEquivalentTo(VerkleTestUtils.ValueEmptyCodeHashValue);
        archiveStore.GetLeaf(VerkleTestUtils.KeyCodeSize.BytesToArray(), stateRoot1).Should().BeEquivalentTo(VerkleTestUtils.ArrayAll0Last2);

        archiveStore.GetLeaf(VerkleTestUtils.KeyVersion.BytesToArray(), stateRoot0).Should().BeEquivalentTo(VerkleTestUtils.EmptyArray);
        archiveStore.GetLeaf(VerkleTestUtils.KeyBalance.BytesToArray(), stateRoot0).Should().BeEquivalentTo(VerkleTestUtils.EmptyArray);
        archiveStore.GetLeaf(VerkleTestUtils.KeyNonce.BytesToArray(), stateRoot0).Should().BeEquivalentTo(VerkleTestUtils.EmptyArray);
        archiveStore.GetLeaf(VerkleTestUtils.KeyCodeCommitment.BytesToArray(), stateRoot0).Should().BeEquivalentTo(VerkleTestUtils.ValueEmptyCodeHashValue);
        archiveStore.GetLeaf(VerkleTestUtils.KeyCodeSize.BytesToArray(), stateRoot0).Should().BeEquivalentTo(VerkleTestUtils.EmptyArray);
    }

    [TestCase(DbMode.MemDb, (ulong)10, 10)]
    [TestCase(DbMode.MemDb, (ulong)20, 10)]
    [TestCase(DbMode.MemDb, (ulong)60, 10)]
    [TestCase(DbMode.MemDb, (ulong)500, 10)]
    [TestCase(DbMode.PersistantDb, (ulong)10, 10)]
    [TestCase(DbMode.PersistantDb, (ulong)20, 10)]
    [TestCase(DbMode.PersistantDb, (ulong)60, 10)]
    [TestCase(DbMode.PersistantDb, (ulong)10, 2)]
    [TestCase(DbMode.MemDb, (ulong)10, 2)]
    [TestCase(DbMode.PersistantDb, (ulong)20, 4)]
    [TestCase(DbMode.MemDb, (ulong)20, 4)]
    [TestCase(DbMode.PersistantDb, (ulong)30, 6)]
    [TestCase(DbMode.MemDb, (ulong)30, 6)]
    [TestCase(DbMode.PersistantDb, (ulong)40, 8)]
    [TestCase(DbMode.MemDb, (ulong)40, 8)]
    // [TestCaseSource(nameof(MultiBlockTest))]
    public void TestArchiveStoreForMultipleBlocks(DbMode dbMode, ulong numBlocks, int blockChunks)
    {
        IDbProvider provider;
        switch (dbMode)
        {
            case DbMode.MemDb:
                provider = VerkleDbFactory.InitDatabase(dbMode, null);
                break;
            case DbMode.PersistantDb:
                provider = VerkleDbFactory.InitDatabase(dbMode, VerkleTestUtils.GetDbPathForTest());
                break;
            case DbMode.ReadOnlyDb:
            default:
                throw new ArgumentOutOfRangeException(nameof(dbMode), dbMode, null);
        }

        var treeStore = new VerkleTreeStore<PersistEveryBlock>(provider, LimboLogs.Instance);
        VerkleTree tree = new VerkleTree(treeStore, LimboLogs.Instance);

        VerkleArchiveStore archiveStore =
            new VerkleArchiveStore(treeStore, provider, LimboLogs.Instance) { BlockChunks = blockChunks };

        Hash256[] keys =
        {
            (Hash256)VerkleTestUtils.KeyVersion, (Hash256)VerkleTestUtils.KeyNonce, (Hash256)VerkleTestUtils.KeyBalance,
            (Hash256)VerkleTestUtils.KeyCodeSize, (Hash256)VerkleTestUtils.KeyCodeCommitment
        };

        var stateRoots = new List<Hash256>();
        ulong i = 0;
        long block = 0;
        while (i < numBlocks)
        {
            foreach (Hash256 key in keys)
            {
                tree.Insert(key, new UInt256(i++).ToBigEndian());
                tree.Commit();
                tree.CommitTree(block++);
                stateRoots.Add(tree.StateRoot);
            }
        }

        i = 0;
        block = 0;
        while (i < numBlocks)
        {
            foreach (Hash256 key in keys)
            {
                byte[]? leaf = archiveStore.GetLeaf(key.Bytes, stateRoots[(int)block++]);
                leaf.Should().BeEquivalentTo(new UInt256(i++).ToBigEndian());
            }
        }
    }
}
