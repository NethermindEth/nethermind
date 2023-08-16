// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using FluentAssertions;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Verkle;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Verkle.Curve;
using Nethermind.Verkle.Tree.Interfaces;
using Nethermind.Verkle.Tree.Proofs;
using Nethermind.Verkle.Tree.Sync;
using Nethermind.Verkle.Tree.Utils;

namespace Nethermind.Verkle.Tree.Test;

public class TestSyncRangesInAHugeVerkleTree
{
    public static Random Random { get; } = new(0);
    public static int numKeys = 2000;
    private static string GetDbPathForTest()
    {
        string tempDir = Path.GetTempPath();
        string dbname = "VerkleTrie_TestID_" + TestContext.CurrentContext.Test.ID;
        return Path.Combine(tempDir, dbname);
    }

    private static IVerkleTrieStore GetVerkleStoreForTest(DbMode dbMode)
    {
        IDbProvider provider;
        switch (dbMode)
        {
            case DbMode.MemDb:
                provider = VerkleDbFactory.InitDatabase(dbMode, null);
                break;
            case DbMode.PersistantDb:
                provider = VerkleDbFactory.InitDatabase(dbMode, GetDbPathForTest());
                break;
            case DbMode.ReadOnlyDb:
            default:
                throw new ArgumentOutOfRangeException(nameof(dbMode), dbMode, null);
        }

        return new VerkleStateStore(provider, LimboLogs.Instance);
    }

    private static VerkleTree GetVerkleTreeForTest(DbMode dbMode)
    {
        IDbProvider provider;
        switch (dbMode)
        {
            case DbMode.MemDb:
                provider = VerkleDbFactory.InitDatabase(dbMode, null);
                return new VerkleTree(provider, LimboLogs.Instance);
            case DbMode.PersistantDb:
                provider = VerkleDbFactory.InitDatabase(dbMode, GetDbPathForTest());
                return new VerkleTree(provider, LimboLogs.Instance);
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

    [TestCase(DbMode.MemDb)]
    [TestCase(DbMode.PersistantDb)]
    public void GetSyncRangeForBigVerkleTree(DbMode dbMode)
    {
        const int pathPoolCount = 100_000;
        const int numBlocks = 200;
        const int leafPerBlock = 10;
        const int blockToGetIteratorFrom = 180;

        IVerkleTrieStore store = TestItem.GetVerkleStore(dbMode);
        VerkleTree tree = new(store, LimboLogs.Instance);

        Pedersen[] pathPool = new Pedersen[pathPoolCount];
        SortedDictionary<Pedersen, byte[]> leafs = new();
        SortedDictionary<Pedersen, byte[]> leafsForSync = new();

        for (int i = 0; i < pathPoolCount; i++)
        {
            byte[] key = new byte[32];
            ((UInt256)i).ToBigEndian(key);
            Pedersen keccak = new Pedersen(key);
            pathPool[i] = keccak;
        }


        for (int leafIndex = 0; leafIndex < 10000; leafIndex++)
        {
            byte[] value = new byte[32];
            Random.NextBytes(value);
            Pedersen path = pathPool[Random.Next(pathPool.Length - 1)];
            tree.Insert(path, value);
            leafs[path] = value;
            leafsForSync[path] = value;
        }

        tree.Commit();
        tree.CommitTree(0);


        VerkleCommitment stateRoot180 = VerkleCommitment.Zero;
        for (int blockNumber = 1; blockNumber <= numBlocks; blockNumber++)
        {
            for (int accountIndex = 0; accountIndex < leafPerBlock; accountIndex++)
            {
                byte[] leafValue = new byte[32];

                Random.NextBytes(leafValue);
                Pedersen path = pathPool[Random.Next(pathPool.Length - 1)];

                if (leafs.ContainsKey(path))
                {
                    if (!(Random.NextSingle() > 0.5)) continue;
                    // Console.WriteLine($"blockNumber:{blockNumber} uKey:{path} uValue:{leafValue.ToHexString()}");
                    tree.Insert(path, leafValue);
                    leafs[path] = leafValue;
                    if(blockToGetIteratorFrom >= blockNumber) leafsForSync[path] = leafValue;
                    // Console.WriteLine("new values");
                }
                else
                {
                    // Console.WriteLine($"blockNumber:{blockNumber} nKey:{path} nValue:{leafValue.ToHexString()}");
                    tree.Insert(path, leafValue);
                    leafs[path] = leafValue;
                    if(blockToGetIteratorFrom >= blockNumber) leafsForSync[path] = leafValue;
                }
            }

            tree.Commit();
            tree.CommitTree(blockNumber);
            if (blockNumber == blockToGetIteratorFrom) stateRoot180 = tree.StateRoot;
        }


        Pedersen[] keysArray = leafs.Keys.ToArray();
        int keyLength = keysArray.Length;
        using IEnumerator<KeyValuePair<byte[], byte[]>> rangeEnum =
            tree._verkleStateStore
                .GetLeafRangeIterator(
                keysArray[keyLength/4].Bytes,
                keysArray[(keyLength*2)/3].Bytes, 180)
                .GetEnumerator();


        while (rangeEnum.MoveNext())
        {
            // Console.WriteLine($"Key:{rangeEnum.Current.Key.ToHexString()} AcValue:{rangeEnum.Current.Value.ToHexString()} ExValue:{leafsForSync[rangeEnum.Current.Key].ToHexString()}");
            Assert.That(rangeEnum.Current.Value.SequenceEqual(leafsForSync[rangeEnum.Current.Key]), Is.True);
        }

        using IEnumerator<PathWithSubTree> rangeEnumSized =
            tree._verkleStateStore
                .GetLeafRangeIterator(
                    keysArray[keyLength/4].StemAsSpan.ToArray(),
                    keysArray[(keyLength*2)/3].StemAsSpan.ToArray(), stateRoot180, 1000)
                .GetEnumerator();


        long bytesSent = 0;
        while (rangeEnumSized.MoveNext())
        {
            Console.WriteLine($"{rangeEnumSized.Current.Path}");
            bytesSent += 31;
            bytesSent = rangeEnumSized.Current.SubTree.Aggregate(bytesSent, (current, xx) => current + 33);
            // Console.WriteLine($"Key:{rangeEnum.Current.Key.ToHexString()} AcValue:{rangeEnum.Current.Value.ToHexString()} ExValue:{leafsForSync[rangeEnum.Current.Key].ToHexString()}");
            // Assert.That(rangeEnum.Current.Value.SequenceEqual(leafsForSync[rangeEnum.Current.Key]), Is.True);
        }
        Console.WriteLine($"{bytesSent}");
    }

    [TestCase(DbMode.MemDb)]
    public void GetSyncRangeForBigVerkleTreeAndHealTree(DbMode dbMode)
    {
        const int pathPoolCount = 100_000;
        const int numBlocks1 = 200;
        const int numBlocks2 = 20;
        const int leafPerBlock = 10;

        IVerkleTrieStore remoteStore = TestItem.GetVerkleStore(dbMode);
        VerkleTree remoteTree = new(remoteStore, LimboLogs.Instance);

        IVerkleTrieStore localStore = TestItem.GetVerkleStore(dbMode);
        VerkleTree localTree = new(localStore, LimboLogs.Instance);

        Pedersen[] pathPool = new Pedersen[pathPoolCount];
        SortedDictionary<Pedersen, byte[]> leafs = new();
        SortedDictionary<Pedersen, byte[]> leafsForSync = new();

        for (int i = 0; i < pathPoolCount; i++)
        {
            byte[] key = new byte[32];
            ((UInt256)i).ToBigEndian(key);
            Pedersen keccak = new Pedersen(key);
            pathPool[i] = keccak;
        }


        for (int leafIndex = 0; leafIndex < 10000; leafIndex++)
        {
            byte[] value = new byte[32];
            Random.NextBytes(value);
            Pedersen path = pathPool[Random.Next(pathPool.Length - 1)];
            remoteTree.Insert(path, value);
            leafs[path] = value;
            leafsForSync[path] = value;
        }

        remoteTree.Commit();
        remoteTree.CommitTree(0);


        for (int blockNumber = 1; blockNumber <= numBlocks1; blockNumber++)
        {
            for (int accountIndex = 0; accountIndex < leafPerBlock; accountIndex++)
            {
                byte[] leafValue = new byte[32];

                Random.NextBytes(leafValue);
                Pedersen path = pathPool[Random.Next(pathPool.Length - 1)];

                if (leafs.ContainsKey(path))
                {
                    if (!(Random.NextSingle() > 0.5)) continue;
                    // Console.WriteLine($"blockNumber:{blockNumber} uKey:{path} uValue:{leafValue.ToHexString()}");
                    remoteTree.Insert(path, leafValue);
                    leafs[path] = leafValue;
                    // Console.WriteLine("new values");
                }
                else
                {
                    // Console.WriteLine($"blockNumber:{blockNumber} nKey:{path} nValue:{leafValue.ToHexString()}");
                    remoteTree.Insert(path, leafValue);
                    leafs[path] = leafValue;
                }
            }

            remoteTree.Commit();
            remoteTree.CommitTree(blockNumber);
        }

        Banderwagon root = default;
        ExecutionWitness executionWitness = default;
        SortedSet<byte[]> update = new(Bytes.Comparer);

        int startingHashIndex = 0;
        int endHashIndex = 0;
        for (int blockNumber = numBlocks1 + 1; blockNumber <= numBlocks1 + 5; blockNumber++)
        {
            for (int i = 0; i < 19; i++)
            {
                endHashIndex = startingHashIndex + 1000;

                PathWithSubTree[] range =
                    remoteTree._verkleStateStore
                        .GetLeafRangeIterator(
                            pathPool[startingHashIndex].StemAsSpan.ToArray(),
                            pathPool[endHashIndex].StemAsSpan.ToArray(),
                            remoteTree.StateRoot, 10000000)
                        .ToArray();
                ProcessSubTreeRange(remoteTree, localTree, blockNumber, remoteTree.StateRoot, range);

                startingHashIndex = endHashIndex + 1;
            }

            if (update.Count != 0)
            {
                // use execution witness to heal
                bool insertedWitness = localTree.InsertIntoStatelessTree(executionWitness, root, false);
                Assert.IsTrue(insertedWitness);
                localTree.CommitTree(0);
            }

            update = new SortedSet<byte[]>(Bytes.Comparer);
            for (int accountIndex = 0; accountIndex < leafPerBlock; accountIndex++)
            {
                byte[] leafValue = new byte[32];
                Random.NextBytes(leafValue);
                Pedersen path = pathPool[Random.Next(pathPool.Length - 1)];

                if (leafs.ContainsKey(path))
                {
                    if (!(Random.NextSingle() > 0.5)) continue;
                    // Console.WriteLine($"blockNumber:{blockNumber} uKey:{path} uValue:{leafValue.ToHexString()}");
                    remoteTree.Insert(path, leafValue);
                    leafs[path] = leafValue;
                    update.Add(path.Bytes);
                    // Console.WriteLine("new values");
                }
                else
                {
                    // Console.WriteLine($"blockNumber:{blockNumber} nKey:{path} nValue:{leafValue.ToHexString()}");
                    remoteTree.Insert(path, leafValue);
                    leafs[path] = leafValue;
                    update.Add(path.Bytes);
                }
            }

            remoteTree.Commit();
            remoteTree.CommitTree(blockNumber);

            executionWitness = remoteTree.GenerateExecutionWitness(update.ToArray(), out root);
        }

        endHashIndex = startingHashIndex + 1000;
        while (endHashIndex < pathPool.Length - 1)
        {
            endHashIndex = startingHashIndex + 1000;
            if (endHashIndex > pathPool.Length - 1)
            {
                endHashIndex = pathPool.Length - 1;
            }

            PathWithSubTree[] range = remoteTree._verkleStateStore.GetLeafRangeIterator(
                pathPool[startingHashIndex].StemAsSpan.ToArray(),
                pathPool[endHashIndex].StemAsSpan.ToArray(),
                remoteTree.StateRoot, 100000000).ToArray();
            ProcessSubTreeRange(remoteTree, localTree, numBlocks1 + numBlocks2, remoteTree.StateRoot, range);

            startingHashIndex += 1000;
        }



        if (update.Count != 0)
        {
            // use execution witness to heal
            bool insertedWitness = localTree.InsertIntoStatelessTree(executionWitness, root, false);
            Assert.IsTrue(insertedWitness);
            localTree.CommitTree(0);
        }

        VerkleTreeDumper oldTreeDumper = new();
        VerkleTreeDumper newTreeDumper = new();

        localTree.Accept(oldTreeDumper, localTree.StateRoot);
        remoteTree.Accept(newTreeDumper, remoteTree.StateRoot);

        Console.WriteLine("oldTreeDumper");
        Console.WriteLine(oldTreeDumper.ToString());
        Console.WriteLine("newTreeDumper");
        Console.WriteLine(newTreeDumper.ToString());

        oldTreeDumper.ToString().Should().BeEquivalentTo(newTreeDumper.ToString());

        Assert.IsTrue(oldTreeDumper.ToString().SequenceEqual(newTreeDumper.ToString()));
    }

    private static void ProcessSubTreeRange(VerkleTree remoteTree, VerkleTree localTree, int blockNumber, VerkleCommitment stateRoot, PathWithSubTree[] subTrees)
    {
        Stem startingStem = subTrees[0].Path;
        Stem endStem = subTrees[^1].Path;
        // Stem limitHash = Stem.MaxValue;

        VerkleProof proof = remoteTree.CreateVerkleRangeProof(startingStem, endStem, out Banderwagon root);

        bool isTrue = localTree.CreateStatelessTreeFromRange(proof, root, startingStem, endStem, subTrees);
        Assert.IsTrue(isTrue);
    }


    [TestCase(DbMode.MemDb)]
    [TestCase(DbMode.PersistantDb)]
    public void CreateHugeTree(DbMode dbMode)
    {
        long block = 0;
        VerkleTree tree = GetVerkleTreeForTest(dbMode);
        Dictionary<byte[], byte[]?> kvMap = new(Bytes.EqualityComparer);
        byte[] key = new byte[32];
        byte[] value = new byte[32];
        DateTime start = DateTime.Now;
        for (int i = 0; i < numKeys; i++)
        {
            Random.NextBytes(key);
            Random.NextBytes(value);
            kvMap[key.AsSpan().ToArray()] = value.AsSpan().ToArray();
            tree.Insert(key, value);
        }
        DateTime check1 = DateTime.Now;
        tree.Commit();
        tree.CommitTree(block++);
        DateTime check2 = DateTime.Now;
        Console.WriteLine($"{block} Insert: {(check1 - start).TotalMilliseconds}");
        Console.WriteLine($"{block} Flush: {(check2 - check1).TotalMilliseconds}");

        SortedSet<byte[]> keys = new(Bytes.Comparer);
        for (int i = 10; i < numKeys; i += 10)
        {
            DateTime check5 = DateTime.Now;
            Random.NextBytes(key);
            Random.NextBytes(value);
            for (int j = (i-10); j < i; j += 1)
            {
                Random.NextBytes(key);
                Random.NextBytes(value);
                kvMap[key.AsSpan().ToArray()] = value.AsSpan().ToArray();
                tree.Insert(key, value);
                keys.Add(key.AsSpan().ToArray());
            }
            DateTime check3 = DateTime.Now;
            tree.Commit();
            tree.CommitTree(block++);
            DateTime check4 = DateTime.Now;
            Console.WriteLine($"{block} Insert: {(check3 - check5).TotalMilliseconds}");
            Console.WriteLine($"{block} Flush: {(check4 - check3).TotalMilliseconds}");
        }
        DateTime check6 = DateTime.Now;
        Console.WriteLine($"Loop Time: {(check6 - check2).TotalMilliseconds}");
        Console.WriteLine($"Total Time: {(check6 - start).TotalMilliseconds}");


        byte[][] keysArray = keys.ToArray();
        using IEnumerator<KeyValuePair<byte[], byte[]>> rangeEnum =
            tree._verkleStateStore.GetLeafRangeIterator(keysArray[30], keysArray[90], 180).GetEnumerator();

        while (rangeEnum.MoveNext())
        {
            Console.WriteLine($"Key:{rangeEnum.Current.Key.ToHexString()} Value:{rangeEnum.Current.Value.ToHexString()}");
            Assert.That(rangeEnum.Current.Value.SequenceEqual(kvMap[rangeEnum.Current.Key]), Is.True);
        }
    }

    [TestCase(DbMode.MemDb)]
    [TestCase(DbMode.PersistantDb)]
    public void TestRangeIterator(DbMode dbMode)
    {
        const int pathPoolCount = 100_000;
        const int leafPerBlock = 10;

        IVerkleTrieStore store = TestItem.GetVerkleStore(dbMode);
        VerkleTree tree = new(store, LimboLogs.Instance);

        Pedersen[] pathPool = new Pedersen[pathPoolCount];
        SortedDictionary<Pedersen, byte[]> leafs = new();

        for (int i = 0; i < pathPoolCount; i++)
        {
            byte[] key = new byte[32];
            ((UInt256)i).ToBigEndian(key);
            Pedersen keccak = new Pedersen(key);
            pathPool[i] = keccak;
        }


        for (int leafIndex = 0; leafIndex < 10000; leafIndex++)
        {
            byte[] value = new byte[32];
            Random.NextBytes(value);
            Pedersen path = pathPool[Random.Next(pathPool.Length - 1)];
            tree.Insert(path, value);
            leafs[path] = value;
            Console.WriteLine($"blockNumber:{0} nKey:{path} nValue:{value.ToHexString()}");
        }

        tree.Commit();
        tree.CommitTree(0);

        for (int blockNumber = 1; blockNumber <= 180; blockNumber++)
        {
            for (int accountIndex = 0; accountIndex < leafPerBlock; accountIndex++)
            {
                byte[] leafValue = new byte[32];

                Random.NextBytes(leafValue);
                Pedersen path = pathPool[Random.Next(pathPool.Length - 1)];

                if (leafs.ContainsKey(path))
                {
                    if (!(Random.NextSingle() > 0.5)) continue;
                    Console.WriteLine($"blockNumber:{blockNumber} uKey:{path} uValue:{leafValue.ToHexString()}");
                    tree.Insert(path, leafValue);
                    leafs[path] = leafValue;
                    Console.WriteLine("new values");
                }
                else
                {
                    Console.WriteLine($"blockNumber:{blockNumber} nKey:{path} nValue:{leafValue.ToHexString()}");
                    tree.Insert(path, leafValue);
                    leafs[path] = leafValue;
                }
            }

            tree.Commit();
            tree.CommitTree(blockNumber);
        }

        KeyValuePair<byte[], byte[]>[] rangeEnum =
            tree._verkleStateStore.GetLeafRangeIterator(Pedersen.Zero.Bytes, Pedersen.MaxValue.Bytes, 180)
                .ToArray();

        int index = 0;
        foreach (KeyValuePair<Pedersen, byte[]> leaf in leafs)
        {
            Console.WriteLine($"{leaf.Key} {rangeEnum[index].Key.ToHexString()}");
            Console.WriteLine($"{leaf.Value.ToHexString()} {rangeEnum[index].Value.ToArray()}");
            Assert.IsTrue(leaf.Key.Bytes.SequenceEqual(rangeEnum[index].Key));
            Assert.IsTrue(leaf.Value.SequenceEqual(rangeEnum[index].Value));
            index++;
        }
    }
}
