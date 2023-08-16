// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core.Verkle;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Verkle.Curve;

namespace Nethermind.Verkle.Tree.Test;

public class StatelessTreeTest
{
    public static Random Random { get; } = new(0);
    public static int numKeys = 1000;
    private static string GetDbPathForTest()
    {
        string tempDir = Path.GetTempPath();
        string dbname = "VerkleTrie_TestID_" + TestContext.CurrentContext.Test.ID;
        return Path.Combine(tempDir, dbname);
    }

    private static VerkleTree GetVerkleTreeForTest(DbMode dbMode)
    {
        IDbProvider provider;
        switch (dbMode)
        {
            case DbMode.MemDb:
                provider = VerkleDbFactory.InitDatabase(dbMode, null);
                return new VerkleTree(provider, SimpleConsoleLogManager.Instance);
            case DbMode.PersistantDb:
                provider = VerkleDbFactory.InitDatabase(dbMode, GetDbPathForTest());
                return new VerkleTree(provider, SimpleConsoleLogManager.Instance);
            case DbMode.ReadOnlyDb:
            default:
                throw new ArgumentOutOfRangeException(nameof(dbMode), dbMode, null);
        }
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

    [TestCase(DbMode.MemDb, 10, 20, 30)]
    [TestCase(DbMode.MemDb, 100, 200, 300)]
    [TestCase(DbMode.MemDb, 1000, 2000, 3000)]
    public void InsertAndCreateStatelessTree(DbMode dbMode, int start, int end, int pathCount)
    {
        VerkleTree initTree = GetVerkleTreeForTest(dbMode);

        Pedersen[] pathPool = new Pedersen[pathCount];
        byte[][] leaf1 = new  byte[pathCount][];
        byte[][] leaf2 = new  byte[pathCount][];

        for (int i = 0; i < pathCount; i++)
        {
            byte[] key = new byte[32];
            ((UInt256)i).ToBigEndian(key);
            Pedersen keccak = new (key);
            pathPool[i] = keccak;

            byte[] valueOld = new byte[32];
            Random.NextBytes(valueOld);
            leaf1[i] = valueOld;

            byte[] valueNew = new byte[32];
            Random.NextBytes(valueNew);
            leaf2[i] = valueNew;
        }



        for (int i = 0; i < pathCount; i++) initTree.Insert(pathPool[i], leaf1[i]);
        initTree.Commit();
        initTree.CommitTree(0);
        Console.WriteLine($"Commit init tree for block 0: {initTree.StateRoot}");



        ExecutionWitness execWitness = initTree.GenerateExecutionWitness(pathPool[start..end].Select(x => x.Bytes).ToArray(), out Banderwagon rootPoint);
        Console.WriteLine($"generated execution witness");



        for (int i = start; i < end; i++) initTree.Insert(pathPool[i], leaf2[i]);
        initTree.Commit();
        initTree.CommitTree(1);
        VerkleCommitment initTreeStateRootBlock1 = initTree.StateRoot;
        Console.WriteLine($"Full Block1 StateRoot: {initTreeStateRootBlock1}");



        VerkleTree statelessTree = GetVerkleTreeForTest(dbMode);
        Console.WriteLine($"init stateless tree");
        statelessTree.InsertIntoStatelessTree(execWitness, rootPoint).Should().BeTrue();
        Console.WriteLine($"create stateless tree and now insert block 1");



        for (int i = start; i < end; i++) statelessTree.Insert(pathPool[i], leaf2[i]);
        statelessTree.Commit();
        statelessTree.CommitTree(1);
        VerkleCommitment statelessTreeStateRootBlock1 = statelessTree.StateRoot;
        Console.WriteLine($"Stateless Block1 StateRoot: {statelessTreeStateRootBlock1}");

        statelessTreeStateRootBlock1.Should().BeEquivalentTo(initTreeStateRootBlock1);
    }
}
