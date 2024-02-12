// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Verkle;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Verkle.Tree;
using Nethermind.Verkle.Tree.History.V1;
using Nethermind.Verkle.Tree.TreeStore;
using Nethermind.Verkle.Tree.Utils;
using Nethermind.Verkle.Tree.VerkleDb;
using NUnit.Framework;

namespace Nethermind.Verkle.Tree.Test;

public class HistoryTests
{
    public readonly struct PersistWithCacheSize3: IPersistenceStrategy
    {
        public static bool IsUsingCache => true;
        public static int CacheSize => 3;
    }

    [TearDown]
    public void CleanTestData()
    {
        string dbPath = VerkleTestUtils.GetDbPathForTest();
        if (Directory.Exists(dbPath))
        {
            Directory.Delete(dbPath, true);
        }
    }

    // one thing to remember while adding tests here is that the current implementation does not support two non
    // consecutive blocks with same state root.

    [TestCase(DbMode.MemDb)]
    [TestCase(DbMode.PersistantDb)]
    public void TestInsertGetMultiBlockReverseState(DbMode dbMode)
    {
        VerkleTree tree = VerkleTestUtils.GetVerkleTreeForTest<PersistWithCacheSize3>(dbMode);

        tree.Insert((Hash256)VerkleTestUtils.KeyVersion, VerkleTestUtils.EmptyArray);
        tree.Insert((Hash256)VerkleTestUtils.KeyBalance, VerkleTestUtils.EmptyArray);
        tree.Insert((Hash256)VerkleTestUtils.KeyNonce, VerkleTestUtils.EmptyArray);
        tree.Insert((Hash256)VerkleTestUtils.KeyCodeCommitment, VerkleTestUtils.ValueEmptyCodeHashValue);
        tree.Insert((Hash256)VerkleTestUtils.KeyCodeSize, VerkleTestUtils.EmptyArray);
        tree.Commit();
        tree.CommitTree(0);
        Hash256 stateRoot0 = tree.StateRoot;

        tree.Get(VerkleTestUtils.KeyVersion).Should().BeEquivalentTo(VerkleTestUtils.EmptyArray);
        tree.Get(VerkleTestUtils.KeyBalance).Should().BeEquivalentTo(VerkleTestUtils.EmptyArray);
        tree.Get(VerkleTestUtils.KeyNonce).Should().BeEquivalentTo(VerkleTestUtils.EmptyArray);
        tree.Get(VerkleTestUtils.KeyCodeCommitment).Should().BeEquivalentTo(VerkleTestUtils.ValueEmptyCodeHashValue);
        tree.Get(VerkleTestUtils.KeyCodeSize).Should().BeEquivalentTo(VerkleTestUtils.EmptyArray);

        tree.Insert((Hash256)VerkleTestUtils.KeyVersion, ((UInt256)1).ToBigEndian());
        tree.Insert((Hash256)VerkleTestUtils.KeyBalance, ((UInt256)1).ToBigEndian());
        tree.Insert((Hash256)VerkleTestUtils.KeyNonce, ((UInt256)1).ToBigEndian());
        tree.Insert((Hash256)VerkleTestUtils.KeyCodeCommitment, ((UInt256)1).ToBigEndian());
        tree.Insert((Hash256)VerkleTestUtils.KeyCodeSize, ((UInt256)1).ToBigEndian());
        tree.Commit();
        tree.CommitTree(1);
        Hash256 stateRoot1 = tree.StateRoot;

        tree.Get(VerkleTestUtils.KeyVersion).Should().BeEquivalentTo(((UInt256)1).ToBigEndian());
        tree.Get(VerkleTestUtils.KeyBalance).Should().BeEquivalentTo(((UInt256)1).ToBigEndian());
        tree.Get(VerkleTestUtils.KeyNonce).Should().BeEquivalentTo(((UInt256)1).ToBigEndian());
        tree.Get(VerkleTestUtils.KeyCodeCommitment).Should().BeEquivalentTo(((UInt256)1).ToBigEndian());
        tree.Get(VerkleTestUtils.KeyCodeSize).Should().BeEquivalentTo(((UInt256)1).ToBigEndian());

        tree.Insert((Hash256)VerkleTestUtils.KeyVersion, ((UInt256)2).ToBigEndian());
        tree.Insert((Hash256)VerkleTestUtils.KeyBalance, ((UInt256)2).ToBigEndian());
        tree.Insert((Hash256)VerkleTestUtils.KeyNonce, ((UInt256)2).ToBigEndian());
        tree.Insert((Hash256)VerkleTestUtils.KeyCodeCommitment, ((UInt256)2).ToBigEndian());
        tree.Insert((Hash256)VerkleTestUtils.KeyCodeSize, ((UInt256)2).ToBigEndian());
        tree.Commit();
        tree.CommitTree(2);
        Hash256 stateRoot2 = tree.StateRoot;

        tree.Get(VerkleTestUtils.KeyVersion).Should().BeEquivalentTo(((UInt256)2).ToBigEndian());
        tree.Get(VerkleTestUtils.KeyBalance).Should().BeEquivalentTo(((UInt256)2).ToBigEndian());
        tree.Get(VerkleTestUtils.KeyNonce).Should().BeEquivalentTo(((UInt256)2).ToBigEndian());
        tree.Get(VerkleTestUtils.KeyCodeCommitment).Should().BeEquivalentTo(((UInt256)2).ToBigEndian());
        tree.Get(VerkleTestUtils.KeyCodeSize).Should().BeEquivalentTo(((UInt256)2).ToBigEndian());

        tree.StateRoot = stateRoot1;

        tree.Get(VerkleTestUtils.KeyVersion).Should().BeEquivalentTo(((UInt256)1).ToBigEndian());
        tree.Get(VerkleTestUtils.KeyBalance).Should().BeEquivalentTo(((UInt256)1).ToBigEndian());
        tree.Get(VerkleTestUtils.KeyNonce).Should().BeEquivalentTo(((UInt256)1).ToBigEndian());
        tree.Get(VerkleTestUtils.KeyCodeCommitment).Should().BeEquivalentTo(((UInt256)1).ToBigEndian());
        tree.Get(VerkleTestUtils.KeyCodeSize).Should().BeEquivalentTo(((UInt256)1).ToBigEndian());

        tree.StateRoot = stateRoot0;

        tree.Get(VerkleTestUtils.KeyVersion).Should().BeEquivalentTo(VerkleTestUtils.EmptyArray);
        tree.Get(VerkleTestUtils.KeyBalance).Should().BeEquivalentTo(VerkleTestUtils.EmptyArray);
        tree.Get(VerkleTestUtils.KeyNonce).Should().BeEquivalentTo(VerkleTestUtils.EmptyArray);
        tree.Get(VerkleTestUtils.KeyCodeCommitment).Should().BeEquivalentTo(VerkleTestUtils.ValueEmptyCodeHashValue);
        tree.Get(VerkleTestUtils.KeyCodeSize).Should().BeEquivalentTo(VerkleTestUtils.EmptyArray);

        tree.StateRoot = stateRoot2;

        tree.Insert((Hash256)VerkleTestUtils.KeyVersion, ((UInt256)3).ToBigEndian());
        tree.Insert((Hash256)VerkleTestUtils.KeyBalance, ((UInt256)3).ToBigEndian());
        tree.Insert((Hash256)VerkleTestUtils.KeyNonce, ((UInt256)3).ToBigEndian());
        tree.Insert((Hash256)VerkleTestUtils.KeyCodeCommitment, ((UInt256)3).ToBigEndian());
        tree.Insert((Hash256)VerkleTestUtils.KeyCodeSize, ((UInt256)3).ToBigEndian());
        tree.Commit();
        tree.CommitTree(3);
        Hash256 stateRoot3 = tree.StateRoot;

        tree.Get(VerkleTestUtils.KeyVersion).Should().BeEquivalentTo(((UInt256)3).ToBigEndian());
        tree.Get(VerkleTestUtils.KeyBalance).Should().BeEquivalentTo(((UInt256)3).ToBigEndian());
        tree.Get(VerkleTestUtils.KeyNonce).Should().BeEquivalentTo(((UInt256)3).ToBigEndian());
        tree.Get(VerkleTestUtils.KeyCodeCommitment).Should().BeEquivalentTo(((UInt256)3).ToBigEndian());
        tree.Get(VerkleTestUtils.KeyCodeSize).Should().BeEquivalentTo(((UInt256)3).ToBigEndian());

        tree.Insert((Hash256)VerkleTestUtils.KeyVersion, ((UInt256)4).ToBigEndian());
        tree.Insert((Hash256)VerkleTestUtils.KeyBalance, ((UInt256)4).ToBigEndian());
        tree.Insert((Hash256)VerkleTestUtils.KeyNonce, ((UInt256)4).ToBigEndian());
        tree.Insert((Hash256)VerkleTestUtils.KeyCodeCommitment, ((UInt256)4).ToBigEndian());
        tree.Insert((Hash256)VerkleTestUtils.KeyCodeSize, ((UInt256)4).ToBigEndian());
        tree.Commit();
        tree.CommitTree(4);
        Hash256 stateRoot4 = tree.StateRoot;

        tree.Get(VerkleTestUtils.KeyVersion).Should().BeEquivalentTo(((UInt256)4).ToBigEndian());
        tree.Get(VerkleTestUtils.KeyBalance).Should().BeEquivalentTo(((UInt256)4).ToBigEndian());
        tree.Get(VerkleTestUtils.KeyNonce).Should().BeEquivalentTo(((UInt256)4).ToBigEndian());
        tree.Get(VerkleTestUtils.KeyCodeCommitment).Should().BeEquivalentTo(((UInt256)4).ToBigEndian());
        tree.Get(VerkleTestUtils.KeyCodeSize).Should().BeEquivalentTo(((UInt256)4).ToBigEndian());

        tree.Insert((Hash256)VerkleTestUtils.KeyVersion, ((UInt256)5).ToBigEndian());
        tree.Insert((Hash256)VerkleTestUtils.KeyBalance, ((UInt256)5).ToBigEndian());
        tree.Insert((Hash256)VerkleTestUtils.KeyNonce, ((UInt256)5).ToBigEndian());
        tree.Insert((Hash256)VerkleTestUtils.KeyCodeCommitment, ((UInt256)5).ToBigEndian());
        tree.Insert((Hash256)VerkleTestUtils.KeyCodeSize, ((UInt256)5).ToBigEndian());
        tree.Commit();
        tree.CommitTree(5);
        Hash256 stateRoot5 = tree.StateRoot;

        tree.Get(VerkleTestUtils.KeyVersion).Should().BeEquivalentTo(((UInt256)5).ToBigEndian());
        tree.Get(VerkleTestUtils.KeyBalance).Should().BeEquivalentTo(((UInt256)5).ToBigEndian());
        tree.Get(VerkleTestUtils.KeyNonce).Should().BeEquivalentTo(((UInt256)5).ToBigEndian());
        tree.Get(VerkleTestUtils.KeyCodeCommitment).Should().BeEquivalentTo(((UInt256)5).ToBigEndian());
        tree.Get(VerkleTestUtils.KeyCodeSize).Should().BeEquivalentTo(((UInt256)5).ToBigEndian());

        tree.Insert((Hash256)VerkleTestUtils.KeyVersion, ((UInt256)6).ToBigEndian());
        tree.Insert((Hash256)VerkleTestUtils.KeyBalance, ((UInt256)6).ToBigEndian());
        tree.Insert((Hash256)VerkleTestUtils.KeyNonce, ((UInt256)6).ToBigEndian());
        tree.Insert((Hash256)VerkleTestUtils.KeyCodeCommitment, ((UInt256)6).ToBigEndian());
        tree.Insert((Hash256)VerkleTestUtils.KeyCodeSize, ((UInt256)6).ToBigEndian());
        tree.Commit();
        tree.CommitTree(6);
        Hash256 stateRoot6 = tree.StateRoot;

        tree.Get(VerkleTestUtils.KeyVersion).Should().BeEquivalentTo(((UInt256)6).ToBigEndian());
        tree.Get(VerkleTestUtils.KeyBalance).Should().BeEquivalentTo(((UInt256)6).ToBigEndian());
        tree.Get(VerkleTestUtils.KeyNonce).Should().BeEquivalentTo(((UInt256)6).ToBigEndian());
        tree.Get(VerkleTestUtils.KeyCodeCommitment).Should().BeEquivalentTo(((UInt256)6).ToBigEndian());
        tree.Get(VerkleTestUtils.KeyCodeSize).Should().BeEquivalentTo(((UInt256)6).ToBigEndian());

        tree.StateRoot = stateRoot4;
        tree.Get(VerkleTestUtils.KeyVersion).Should().BeEquivalentTo(((UInt256)4).ToBigEndian());
        tree.Get(VerkleTestUtils.KeyBalance).Should().BeEquivalentTo(((UInt256)4).ToBigEndian());
        tree.Get(VerkleTestUtils.KeyNonce).Should().BeEquivalentTo(((UInt256)4).ToBigEndian());
        tree.Get(VerkleTestUtils.KeyCodeCommitment).Should().BeEquivalentTo(((UInt256)4).ToBigEndian());
        tree.Get(VerkleTestUtils.KeyCodeSize).Should().BeEquivalentTo(((UInt256)4).ToBigEndian());

        tree.StateRoot = stateRoot5;
        tree.Get(VerkleTestUtils.KeyVersion).Should().BeEquivalentTo(((UInt256)5).ToBigEndian());
        tree.Get(VerkleTestUtils.KeyBalance).Should().BeEquivalentTo(((UInt256)5).ToBigEndian());
        tree.Get(VerkleTestUtils.KeyNonce).Should().BeEquivalentTo(((UInt256)5).ToBigEndian());
        tree.Get(VerkleTestUtils.KeyCodeCommitment).Should().BeEquivalentTo(((UInt256)5).ToBigEndian());
        tree.Get(VerkleTestUtils.KeyCodeSize).Should().BeEquivalentTo(((UInt256)5).ToBigEndian());
    }

    [TestCase(DbMode.MemDb)]
    [TestCase(DbMode.PersistantDb)]
    public void TestInsertGetBatchMultiBlockReverseState(DbMode dbMode)
    {
        VerkleTree tree = VerkleTestUtils.GetVerkleTreeForTest<VerkleSyncCache>(dbMode);

        tree.Insert(VerkleTestUtils.KeyVersion, VerkleTestUtils.EmptyArray);
        tree.Insert(VerkleTestUtils.KeyBalance, VerkleTestUtils.EmptyArray);
        tree.Insert(VerkleTestUtils.KeyNonce, VerkleTestUtils.EmptyArray);
        tree.Insert(VerkleTestUtils.KeyCodeCommitment, VerkleTestUtils.ValueEmptyCodeHashValue);
        tree.Insert(VerkleTestUtils.KeyCodeSize, VerkleTestUtils.EmptyArray);
        tree.Commit();
        tree.CommitTree(0);
        Hash256 stateRoot0 = tree.StateRoot;

        tree.Get(VerkleTestUtils.KeyVersion).Should().BeEquivalentTo(VerkleTestUtils.EmptyArray);
        tree.Get(VerkleTestUtils.KeyBalance).Should().BeEquivalentTo(VerkleTestUtils.EmptyArray);
        tree.Get(VerkleTestUtils.KeyNonce).Should().BeEquivalentTo(VerkleTestUtils.EmptyArray);
        tree.Get(VerkleTestUtils.KeyCodeCommitment).Should().BeEquivalentTo(VerkleTestUtils.ValueEmptyCodeHashValue);
        tree.Get(VerkleTestUtils.KeyCodeSize).Should().BeEquivalentTo(VerkleTestUtils.EmptyArray);

        tree.Insert(VerkleTestUtils.KeyVersion, VerkleTestUtils.ArrayAll0Last2);
        tree.Insert(VerkleTestUtils.KeyBalance, VerkleTestUtils.ArrayAll0Last2);
        tree.Insert(VerkleTestUtils.KeyNonce, VerkleTestUtils.ArrayAll0Last2);
        tree.Insert(VerkleTestUtils.KeyCodeCommitment, VerkleTestUtils.ValueEmptyCodeHashValue);
        tree.Insert(VerkleTestUtils.KeyCodeSize, VerkleTestUtils.ArrayAll0Last2);
        tree.Commit();
        tree.CommitTree(1);

        tree.Get(VerkleTestUtils.KeyVersion).Should().BeEquivalentTo(VerkleTestUtils.ArrayAll0Last2);
        tree.Get(VerkleTestUtils.KeyBalance).Should().BeEquivalentTo(VerkleTestUtils.ArrayAll0Last2);
        tree.Get(VerkleTestUtils.KeyNonce).Should().BeEquivalentTo(VerkleTestUtils.ArrayAll0Last2);
        tree.Get(VerkleTestUtils.KeyCodeCommitment).Should().BeEquivalentTo(VerkleTestUtils.ValueEmptyCodeHashValue);
        tree.Get(VerkleTestUtils.KeyCodeSize).Should().BeEquivalentTo(VerkleTestUtils.ArrayAll0Last2);

        tree.Insert(VerkleTestUtils.KeyVersion, VerkleTestUtils.ArrayAll0Last3);
        tree.Insert(VerkleTestUtils.KeyBalance, VerkleTestUtils.ArrayAll0Last3);
        tree.Insert(VerkleTestUtils.KeyNonce, VerkleTestUtils.ArrayAll0Last3);
        tree.Insert(VerkleTestUtils.KeyCodeCommitment, VerkleTestUtils.ValueEmptyCodeHashValue);
        tree.Insert(VerkleTestUtils.KeyCodeSize, VerkleTestUtils.ArrayAll0Last3);
        tree.Commit();
        tree.CommitTree(2);

        tree.Get(VerkleTestUtils.KeyVersion).Should().BeEquivalentTo(VerkleTestUtils.ArrayAll0Last3);
        tree.Get(VerkleTestUtils.KeyBalance).Should().BeEquivalentTo(VerkleTestUtils.ArrayAll0Last3);
        tree.Get(VerkleTestUtils.KeyNonce).Should().BeEquivalentTo(VerkleTestUtils.ArrayAll0Last3);
        tree.Get(VerkleTestUtils.KeyCodeCommitment).Should().BeEquivalentTo(VerkleTestUtils.ValueEmptyCodeHashValue);
        tree.Get(VerkleTestUtils.KeyCodeSize).Should().BeEquivalentTo(VerkleTestUtils.ArrayAll0Last3);

        tree.StateRoot = stateRoot0;

        tree.Get(VerkleTestUtils.KeyVersion).Should().BeEquivalentTo(VerkleTestUtils.EmptyArray);
        tree.Get(VerkleTestUtils.KeyBalance).Should().BeEquivalentTo(VerkleTestUtils.EmptyArray);
        tree.Get(VerkleTestUtils.KeyNonce).Should().BeEquivalentTo(VerkleTestUtils.EmptyArray);
        tree.Get(VerkleTestUtils.KeyCodeCommitment).Should().BeEquivalentTo(VerkleTestUtils.ValueEmptyCodeHashValue);
        tree.Get(VerkleTestUtils.KeyCodeSize).Should().BeEquivalentTo(VerkleTestUtils.EmptyArray);
    }

    [TestCase(DbMode.MemDb)]
    [TestCase(DbMode.PersistantDb)]
    public void TreeMustBeSameWithAndWithoutHistory(DbMode dbMode)
    {
        Random random = new(0);
        int numKeys = 2000;

        long block = 0;

        string dir1 = VerkleTestUtils.GetDbPathForTest() + "a";
        string dir2 = VerkleTestUtils.GetDbPathForTest() + "b";
        try
        {
            VerkleTree treeWithH = VerkleTestUtils.GetVerkleTreeForTest<VerkleSyncCache>(dbMode, dir1);
            VerkleTree treeWithoutH = VerkleTestUtils.GetVerkleTreeForTest<PersistEveryBlock>(dbMode, dir2);

            byte[] key = new byte[32];
            byte[] value = new byte[32];
            for (int i = 0; i < numKeys; i++)
            {
                random.NextBytes(key);
                random.NextBytes(value);
                treeWithH.Insert((Hash256)key.ToArray(), value.ToArray());
                treeWithoutH.Insert((Hash256)key.ToArray(), value.ToArray());
            }
            treeWithH.Commit();
            treeWithoutH.Commit();

            Console.WriteLine(block);
            Assert.IsTrue(treeWithH.StateRoot == treeWithoutH.StateRoot);
            treeWithH.CommitTree(block);
            treeWithoutH.CommitTree(block);
            block++;
            for (int i = 10; i < numKeys; i += 10)
            {
                random.NextBytes(key);
                random.NextBytes(value);
                for (int j = 0; j < 10; j += 1)
                {
                    random.NextBytes(key);
                    random.NextBytes(value);
                    treeWithH.Insert((Hash256)key.ToArray(), value.ToArray());
                    treeWithoutH.Insert((Hash256)key.ToArray(), value.ToArray());
                }
                treeWithH.Commit();
                treeWithoutH.Commit();
                Console.WriteLine(block);
                Assert.IsTrue(treeWithH.StateRoot == treeWithoutH.StateRoot);
                treeWithH.CommitTree(block);
                treeWithoutH.CommitTree(block);
                block++;
            }

            Assert.IsTrue(treeWithH.StateRoot == treeWithoutH.StateRoot);

            var dump1 = new VerkleTreeDumper();
            var dump2 = new VerkleTreeDumper();

            treeWithH.Accept(dump1, treeWithH.StateRoot);
            treeWithoutH.Accept(dump2, treeWithoutH.StateRoot);

            string data1 = dump1.ToString();
            string data2 = dump2.ToString();

            Console.WriteLine("oldTree");
            Console.WriteLine(data1);
            Console.WriteLine("newTree");
            Console.WriteLine(data2);

            data1.Should().BeEquivalentTo(data2);
        }
        finally
        {
            if (Directory.Exists(dir1))
            {
                Directory.Delete(dir1, true);
            }
            if (Directory.Exists(dir2))
            {
                Directory.Delete(dir2, true);
            }
        }
    }
}
