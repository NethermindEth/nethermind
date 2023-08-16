// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Logging;

namespace Nethermind.Verkle.Tree.Test;

[TestFixture]
public class InMemoryDiffTests
{
    public static Random Random { get; } = new();
    public static int numKeys = 2000;
    private static string GetDbPathForTest()
    {
        string tempDir = Path.GetTempPath();
        string dbname = "VerkleTrie_TestID_" + TestContext.CurrentContext.Test.ID;
        return Path.Combine(tempDir, dbname);
    }

    [TearDown]
    public void CleanTestData()
    {
        string dbPath = GetDbPathForTest();
        if (Directory.Exists(dbPath))
        {
            Directory.Delete(dbPath, true);
        }
    }

    private static VerkleTree GetVerkleTreeForTest(DbMode dbMode, int maxBlockInHistory)
    {
        IDbProvider provider;
        VerkleStateStore store;
        switch (dbMode)
        {
            case DbMode.MemDb:
                provider = VerkleDbFactory.InitDatabase(dbMode, null);
                store = new VerkleStateStore(provider, LimboLogs.Instance, maxBlockInHistory);
                return new VerkleTree(store, LimboLogs.Instance);
            case DbMode.PersistantDb:
                provider = VerkleDbFactory.InitDatabase(dbMode, GetDbPathForTest() + maxBlockInHistory);
                store = new VerkleStateStore(provider, LimboLogs.Instance, maxBlockInHistory);
                return new VerkleTree(store, LimboLogs.Instance);
            case DbMode.ReadOnlyDb:
            default:
                throw new ArgumentOutOfRangeException(nameof(dbMode), dbMode, null);
        }
    }

    [TestCase(DbMode.MemDb)]
    public void InsertHugeTree(DbMode dbMode)
    {
        long block = 0;

        VerkleTree treeWithH = GetVerkleTreeForTest(dbMode, 128);
        VerkleTree treeWithoutH = GetVerkleTreeForTest(dbMode, 0);

        byte[] key = new byte[32];
        byte[] value = new byte[32];
        for (int i = 0; i < numKeys; i++)
        {
            Random.NextBytes(key);
            Random.NextBytes(value);
            treeWithH.Insert(key, value);
            treeWithoutH.Insert(key, value);
        }
        treeWithH.Commit();
        treeWithoutH.Commit();
        treeWithH.CommitTree(block++);
        treeWithoutH.CommitTree(block++);
        for (int i = 10; i < numKeys; i += 10)
        {
            Random.NextBytes(key);
            Random.NextBytes(value);
            for (int j = 0; j < 10; j += 1)
            {
                Random.NextBytes(key);
                Random.NextBytes(value);
                treeWithH.Insert(key, value);
                treeWithoutH.Insert(key, value);
            }
            treeWithH.Commit();
            treeWithoutH.Commit();
            treeWithH.CommitTree(block++);
            treeWithoutH.CommitTree(block++);
        }


        VerkleTreeDumper dump1 = new VerkleTreeDumper();
        VerkleTreeDumper dump2 = new VerkleTreeDumper();

        treeWithH.Accept(dump1, treeWithH.StateRoot);
        treeWithoutH.Accept(dump2, treeWithoutH.StateRoot);

        string data1 = dump1.ToString();
        string data2 = dump2.ToString();

        data1.Should().BeEquivalentTo(data2);
    }
}
