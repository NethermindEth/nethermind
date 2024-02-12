// // SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// // SPDX-License-Identifier: LGPL-3.0-only
//
// using System;
// using System.Collections.Generic;
// using System.Collections.Immutable;
// using System.IO;
// using System.Linq;
// using DotNetty.Buffers;
// using FluentAssertions;
// using FluentAssertions.Equivalency;
// using Microsoft.Extensions.ObjectPool;
// using Nethermind.Core.Crypto;
// using Nethermind.Core.Extensions;
// using Nethermind.Core.Test.Builders;
// using Nethermind.Core.Verkle;
// using Nethermind.Db;
// using Nethermind.Db.Rocks;
// using Nethermind.Logging;
// using Nethermind.Network.P2P.Subprotocols.Verkle.Messages;
// using Nethermind.Serialization.Rlp;
// using Nethermind.Synchronization.VerkleSync;
// using Nethermind.Verkle.Curve;
// using Nethermind.Verkle.Tree.Serializers;
// using Nethermind.Verkle.Tree.Sync;
// using Nethermind.Verkle.Tree.TreeStore;
//
// namespace Nethermind.Verkle.Tree.Test;
//
// public class TestSyncRangesInAHugeVerkleTree
// {
//     private static Random Random { get; } = new(0);
//     private const int NumKeys = 2000;
//
//     [TearDown]
//     public void CleanTestData()
//     {
//         string dbPath = VerkleTestUtils.GetDbPathForTest();
//         if (Directory.Exists(dbPath))
//         {
//             Directory.Delete(dbPath, true);
//         }
//     }
//
//     private static Hash256[] GetPedersenPathPool(int pathPoolCount)
//     {
//         var pathPool = new Hash256[pathPoolCount];
//         for (int i = 0; i < pathPoolCount; i++)
//         {
//             pathPool[i] = TestItem.GetRandomKeccak(Random);
//         }
//         return pathPool.ToImmutableSortedSet().ToArray();
//     }
//
//     [TestCase(DbMode.MemDb)]
//     [TestCase(DbMode.PersistantDb)]
//     public void GetSyncRangeForBigVerkleTree(DbMode dbMode)
//     {
//         const int pathPoolCount = 100_0;
//         const int numBlocks = 200;
//         const int leafPerBlock = 2;
//         const int blockToGetIteratorFrom = 180;
//         const int initialLeafCount = 100;
//
//         var localDbProvider = VerkleDbFactory.InitDatabase(DbMode.MemDb, null);
//         var localStore = new VerkleTreeStore<VerkleSyncCache>(localDbProvider, LimboLogs.Instance);
//         var localTree = new VerkleTree(localStore, LimboLogs.Instance);
//         ObjectPool<IVerkleTreeStore> trieStorePool = new DefaultObjectPool<IVerkleTreeStore>(new TrieStorePoolPolicy(localDbProvider, SimpleConsoleLogManager.Instance));
//
//         IVerkleTreeStore store = TestItem.GetVerkleStore<VerkleSyncCache>(dbMode);
//         VerkleTree tree = new(store, LimboLogs.Instance);
//
//         Hash256[] pathPool = GetPedersenPathPool(pathPoolCount);
//         SortedDictionary<Hash256, byte[]> leafs = new();
//         SortedDictionary<Hash256, byte[]> leafsForSync = new();
//
//         for (int leafIndex = 0; leafIndex < initialLeafCount; leafIndex++)
//         {
//             byte[] value = new byte[32];
//             Random.NextBytes(value);
//             Hash256 path = pathPool[Random.Next(pathPool.Length - 1)];
//             tree.Insert(path, value);
//             localTree.Insert(path, value);
//             leafs[path] = value;
//             leafsForSync[path] = value;
//         }
//
//         tree.Commit();
//         localTree.Commit();
//         tree.CommitTree(0);
//         localTree.CommitTree(0);
//
//
//         Hash256 stateRoot180 = Hash256.Zero;
//         for (int blockNumber = 1; blockNumber <= numBlocks; blockNumber++)
//         {
//             for (int accountIndex = 0; accountIndex < leafPerBlock; accountIndex++)
//             {
//                 byte[] leafValue = new byte[32];
//
//                 Random.NextBytes(leafValue);
//                 Hash256 path = pathPool[Random.Next(pathPool.Length - 1)];
//
//                 if (leafs.ContainsKey(path))
//                 {
//                     if (!(Random.NextSingle() > 0.5)) continue;
//                     // Console.WriteLine($"blockNumber:{blockNumber} uKey:{path} uValue:{leafValue.ToHexString()}");
//                     tree.Insert(path, leafValue);
//                     leafs[path] = leafValue;
//                     if(blockToGetIteratorFrom >= blockNumber) leafsForSync[path] = leafValue;
//                     // Console.WriteLine("new values");
//                 }
//                 else
//                 {
//                     // Console.WriteLine($"blockNumber:{blockNumber} nKey:{path} nValue:{leafValue.ToHexString()}");
//                     tree.Insert(path, leafValue);
//                     leafs[path] = leafValue;
//                     if(blockToGetIteratorFrom >= blockNumber) leafsForSync[path] = leafValue;
//                 }
//             }
//
//             tree.Commit();
//             tree.CommitTree(blockNumber);
//             if (blockNumber == blockToGetIteratorFrom) stateRoot180 = tree.StateRoot;
//         }
//
//         SimpleConsoleLogger.Instance.Info("THIS IS PROVING");
//
//         IVerkleTreeStore? poolStore = trieStorePool.Get();
//         Stem startStem = Bytes.FromHexString("0x00000000000000000000000000000000000000000000000000000000000000");
//         Stem limitStem = Bytes.FromHexString("0x20000000000000000000000000000000000000000000000000000000000000");
//         TestAndAssertSyncRanges(tree, poolStore, stateRoot180, startStem, limitStem);
//         trieStorePool.Return(poolStore);
//
//         poolStore = trieStorePool.Get();
//         startStem = Bytes.FromHexString("0x20000000000000000000000000000000000000000000000000000000000000");
//         limitStem = Bytes.FromHexString("0x40000000000000000000000000000000000000000000000000000000000000");
//         TestAndAssertSyncRanges(tree, poolStore, stateRoot180, startStem, limitStem);
//         trieStorePool.Return(poolStore);
//
//         poolStore = trieStorePool.Get();
//         startStem = Bytes.FromHexString("0x40000000000000000000000000000000000000000000000000000000000000");
//         limitStem = Bytes.FromHexString("0x60000000000000000000000000000000000000000000000000000000000000");
//         TestAndAssertSyncRanges(tree, poolStore, stateRoot180, startStem, limitStem);
//         trieStorePool.Return(poolStore);
//
//         poolStore = trieStorePool.Get();
//         startStem = Bytes.FromHexString("0x60000000000000000000000000000000000000000000000000000000000000");
//         limitStem = Bytes.FromHexString("0x80000000000000000000000000000000000000000000000000000000000000");
//         TestAndAssertSyncRanges(tree, poolStore, stateRoot180, startStem, limitStem);
//         trieStorePool.Return(poolStore);
//
//         poolStore = trieStorePool.Get();
//         startStem = Bytes.FromHexString("0x80000000000000000000000000000000000000000000000000000000000000");
//         limitStem = Bytes.FromHexString("0xa0000000000000000000000000000000000000000000000000000000000000");
//         TestAndAssertSyncRanges(tree, poolStore, stateRoot180, startStem, limitStem);
//         trieStorePool.Return(poolStore);
//
//         poolStore = trieStorePool.Get();
//         startStem = Bytes.FromHexString("0xa0000000000000000000000000000000000000000000000000000000000000");
//         limitStem = Bytes.FromHexString("0xc0000000000000000000000000000000000000000000000000000000000000");
//         TestAndAssertSyncRanges(tree, poolStore, stateRoot180, startStem, limitStem);
//         trieStorePool.Return(poolStore);
//
//         poolStore = trieStorePool.Get();
//         startStem = Bytes.FromHexString("0xc0000000000000000000000000000000000000000000000000000000000000");
//         limitStem = Bytes.FromHexString("0xe0000000000000000000000000000000000000000000000000000000000000");
//         TestAndAssertSyncRanges(tree, poolStore, stateRoot180, startStem, limitStem);
//         trieStorePool.Return(poolStore);
//
//         poolStore = trieStorePool.Get();
//         startStem = Bytes.FromHexString("0xe0000000000000000000000000000000000000000000000000000000000000");
//         limitStem = Bytes.FromHexString("0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff");
//         TestAndAssertSyncRanges(tree, poolStore, stateRoot180, startStem, limitStem);
//         poolStore.InsertRootNodeAfterSyncCompletion(stateRoot180.BytesToArray(), 180);
//         trieStorePool.Return(poolStore);
//
//         Assert.IsTrue(localTree.HasStateForStateRoot(stateRoot180));
//
//         VerkleTreeDumper oldTreeDumper = new();
//         VerkleTreeDumper newTreeDumper = new();
//
//         localTree.Accept(oldTreeDumper, stateRoot180);
//         tree.Accept(newTreeDumper, stateRoot180);
//
//         Console.WriteLine("oldTreeDumper");
//         Console.WriteLine(oldTreeDumper.ToString());
//         Console.WriteLine("newTreeDumper");
//         Console.WriteLine(newTreeDumper.ToString());
//
//         oldTreeDumper.ToString().Should().BeEquivalentTo(newTreeDumper.ToString());
//         Assert.IsTrue(oldTreeDumper.ToString().SequenceEqual(newTreeDumper.ToString()));
//
//     }
//
//     public void TestAndAssertSyncRanges(VerkleTree tree, IVerkleTreeStore localStore, Hash256 stateRootToUse, Stem startStem, Stem limitStem)
//     {
//         PathWithSubTree[]? range =
//             tree._verkleStateStore
//                 .GetLeafRangeIterator(
//                     startStem,
//                     limitStem, stateRootToUse,10000000)
//                 .ToArray();
//
//         Stem endStem = range[^1].Path;
//
//         VerkleProof proof = tree.CreateVerkleRangeProof(startStem, endStem, out Banderwagon root, stateRootToUse);
//         var stateStore = new VerkleTreeStore<PersistEveryBlock>(new MemDb(), new MemDb(), new MemDb(), LimboLogs.Instance);
//         var tempTree = new VerkleTree(stateStore, LimboLogs.Instance);
//         bool isTrue = tempTree.CreateStatelessTreeFromRange(proof, root, startStem, endStem, range);
//         Assert.IsTrue(isTrue);
//         localStore.InsertSyncBatch(0, tempTree._treeCache);
//     }
//
//     [TestCase(DbMode.MemDb)]
//     [TestCase(DbMode.PersistantDb)]
//     public void GetSyncRangeForBigVerkleTreeAndHealTree(DbMode dbMode)
//     {
//         const int pathPoolCount = 100_00;
//         const int numBlocks1 = 150;
//         const int numBlocks2 = 10;
//         const int leafPerBlock = 2;
//         const int initialLeafCount = 10000;
//         const int blockJump = 5;
//         const int healingLoop = 5;
//         const int numPathInOneHealingLoop = pathPoolCount / (healingLoop * blockJump);
//
//         IVerkleTreeStore remoteStore = TestItem.GetVerkleStore<VerkleSyncCache>(dbMode);
//         VerkleTree remoteTree = new(remoteStore, LimboLogs.Instance);
//
//         IVerkleTreeStore localStore = TestItem.GetVerkleStore<VerkleSyncCache>(DbMode.MemDb);
//         VerkleTree localTree = new(localStore, LimboLogs.Instance);
//
//         Hash256[] pathPool = GetPedersenPathPool(pathPoolCount);
//         SortedDictionary<Hash256, byte[]> leafs = new();
//
//
//         for (int leafIndex = 0; leafIndex < initialLeafCount; leafIndex++)
//         {
//             byte[] value = new byte[32];
//             Random.NextBytes(value);
//             Hash256 path = pathPool[Random.Next(pathPool.Length - 1)];
//             remoteTree.Insert(path, value);
//             leafs[path] = value;
//         }
//         remoteTree.Commit();
//         remoteTree.CommitTree(0);
//
//
//         for (int blockNumber = 1; blockNumber <= numBlocks1; blockNumber++)
//         {
//             for (int accountIndex = 0; accountIndex < leafPerBlock; accountIndex++)
//             {
//                 byte[] leafValue = new byte[32];
//
//                 Random.NextBytes(leafValue);
//                 Hash256 path = pathPool[Random.Next(pathPool.Length - 1)];
//
//                 if (leafs.ContainsKey(path))
//                 {
//                     if (!(Random.NextSingle() > 0.5)) continue;
//                     remoteTree.Insert(path, leafValue);
//                     leafs[path] = leafValue;
//                 }
//                 else
//                 {
//                     remoteTree.Insert(path, leafValue);
//                     leafs[path] = leafValue;
//                 }
//             }
//
//             remoteTree.Commit();
//             remoteTree.CommitTree(blockNumber);
//         }
//
//         Banderwagon root = default;
//         ExecutionWitness executionWitness = default;
//         SortedSet<byte[]> update = new(Bytes.Comparer);
//
//         int startingHashIndex = 0;
//         int endHashIndex = 0;
//         for (int blockNumber = numBlocks1 + 1; blockNumber <= numBlocks1 + blockJump; blockNumber++)
//         {
//             for (int i = 0; i < healingLoop; i++)
//             {
//                 endHashIndex = startingHashIndex + numPathInOneHealingLoop - 1;
//                 Console.WriteLine($"{startingHashIndex} {endHashIndex}");
//                 PathWithSubTree[] range =
//                     remoteTree._verkleStateStore
//                         .GetLeafRangeIterator(
//                             pathPool[startingHashIndex].Bytes.Slice(0,31).ToArray(),
//                             pathPool[endHashIndex].Bytes.Slice(0,31).ToArray(),
//                             remoteTree.StateRoot, 10000000)
//                         .ToArray();
//                 ProcessSubTreeRange(remoteTree, localStore, blockNumber, remoteTree.StateRoot, range);
//
//                 startingHashIndex = endHashIndex + 1;
//             }
//
//             if (update.Count != 0)
//             {
//                 // use execution witness to heal
//                 bool insertedWitness = localTree.InsertIntoStatelessTree(executionWitness, root, false);
//                 Assert.IsTrue(insertedWitness);
//                 localTree.CommitTree(0);
//             }
//
//             update = new SortedSet<byte[]>(Bytes.Comparer);
//             for (int accountIndex = 0; accountIndex < leafPerBlock; accountIndex++)
//             {
//                 byte[] leafValue = new byte[32];
//                 Random.NextBytes(leafValue);
//                 Hash256 path = pathPool[Random.Next(pathPool.Length - 1)];
//
//                 if (leafs.ContainsKey(path))
//                 {
//                     if (!(Random.NextSingle() > 0.5)) continue;
//                     remoteTree.Insert(path, leafValue);
//                     leafs[path] = leafValue;
//                     update.Add(path.Bytes.ToArray());
//                 }
//                 else
//                 {
//                     remoteTree.Insert(path, leafValue);
//                     leafs[path] = leafValue;
//                     update.Add(path.Bytes.ToArray());
//                 }
//             }
//
//             remoteTree.Commit();
//             remoteTree.CommitTree(blockNumber);
//
//             executionWitness = remoteTree.GenerateExecutionWitness(update.ToArray(), out root);
//         }
//
//         endHashIndex = startingHashIndex + 1000;
//         while (endHashIndex < pathPool.Length - 1)
//         {
//             endHashIndex = startingHashIndex + 1000;
//             if (endHashIndex > pathPool.Length - 1)
//             {
//                 endHashIndex = pathPool.Length - 1;
//             }
//             Console.WriteLine($"{startingHashIndex} {endHashIndex}");
//
//             PathWithSubTree[] range = remoteTree._verkleStateStore.GetLeafRangeIterator(
//                 pathPool[startingHashIndex].Bytes.Slice(0,31).ToArray(),
//                 pathPool[endHashIndex].Bytes.Slice(0,31).ToArray(),
//                 remoteTree.StateRoot, 100000000).ToArray();
//             ProcessSubTreeRange(remoteTree, localStore, numBlocks1 + numBlocks2, remoteTree.StateRoot, range);
//
//             startingHashIndex += 1000;
//         }
//
//
//
//         if (update.Count != 0)
//         {
//             // use execution witness to heal
//             bool insertedWitness = localTree.InsertIntoStatelessTree(executionWitness, root, false);
//             Assert.IsTrue(insertedWitness);
//             localTree.CommitTree(0);
//         }
//
//         VerkleTreeDumper oldTreeDumper = new();
//         VerkleTreeDumper newTreeDumper = new();
//
//         localTree.Accept(oldTreeDumper, localTree.StateRoot);
//         remoteTree.Accept(newTreeDumper, remoteTree.StateRoot);
//
//         Console.WriteLine("oldTreeDumper");
//         Console.WriteLine(oldTreeDumper.ToString());
//         Console.WriteLine("newTreeDumper");
//         Console.WriteLine(newTreeDumper.ToString());
//
//         oldTreeDumper.ToString().Should().BeEquivalentTo(newTreeDumper.ToString());
//
//         Assert.IsTrue(oldTreeDumper.ToString().SequenceEqual(newTreeDumper.ToString()));
//     }
//
//     private static SubTreeRangeMessage TestSubTreeRangeSerializer(SubTreeRangeMessage message)
//     {
//         IByteBuffer buffer = PooledByteBufferAllocator.Default.Buffer(1024 * 16);
//         SubTreeRangeMessageSerializer ser = new();
//         ser.Serialize(buffer, message);
//         SubTreeRangeMessage? data = ser.Deserialize(buffer);
//
//         data.Should().BeEquivalentTo(message, options =>
//         {
//             EquivalencyAssertionOptions<SubTreeRangeMessage>? excluded = options.Excluding(c => c.Name == "RlpLength");
//             return excluded
//                 .Using<Memory<byte>>((context => context.Subject.AsArray().Should().BeEquivalentTo(context.Expectation.AsArray())))
//                 .WhenTypeIs<Memory<byte>>();
//         });
//         return data;
//     }
//
//     private static VerkleProof TestProofSerialization(VerkleProof proof)
//     {
//         VerkleProofSerializer ser = new();
//         VerkleProof data = ser.Decode(new RlpStream(proof.EncodeRlp()));
//         // data.Should().BeEquivalentTo(proof, options =>
//         // {
//         //     EquivalencyAssertionOptions<VerkleProof>? excluded = options.Excluding(c => c.Name == "RlpLength");
//         //     return excluded
//         //         .Using<Memory<byte>>((context => context.Subject.AsArray().Should().BeEquivalentTo(context.Expectation.AsArray())))
//         //         .WhenTypeIs<Memory<byte>>();
//         // });
//         return data;
//     }
//
//     private static void ProcessSubTreeRange(VerkleTree remoteTree, IVerkleTreeStore localStore, int blockNumber, Hash256 stateRoot, PathWithSubTree[] subTrees)
//     {
//         Stem startingStem = subTrees[0].Path;
//         Stem endStem = subTrees[^1].Path;
//         // Stem limitHash = Stem.MaxValue;
//
//         VerkleProof proof = remoteTree.CreateVerkleRangeProof(startingStem, endStem, out Banderwagon root, stateRoot);
//
//         var message = new SubTreeRangeMessage()
//         {
//             PathsWithSubTrees = subTrees,
//             Proofs = proof.EncodeRlp()
//         };
//
//         VerkleProof newProof = TestProofSerialization(proof);
//         SubTreeRangeMessage? newMessage = TestSubTreeRangeSerializer(message);
//         var newStore =
//             new VerkleTreeStore<PersistEveryBlock>(new MemDb(), new MemDb(), new MemDb(), LimboLogs.Instance);
//
//         var localTree = new VerkleTree(newStore, LimboLogs.Instance);
//         bool isTrue = localTree.CreateStatelessTreeFromRange(newProof, root, startingStem, endStem, newMessage.PathsWithSubTrees);
//         Assert.That(isTrue, Is.True);
//         localStore.InsertSyncBatch(0, localTree._treeCache);
//         localStore.InsertRootNodeAfterSyncCompletion(stateRoot.BytesToArray(), 0);
//     }
//
//
//     [TestCase(DbMode.MemDb)]
//     [TestCase(DbMode.PersistantDb)]
//     public void CreateHugeTree(DbMode dbMode)
//     {
//         long block = 0;
//         VerkleTree tree = VerkleTestUtils.GetVerkleTreeForTest<VerkleSyncCache>(dbMode);
//         Dictionary<byte[], byte[]?> kvMap = new(Bytes.EqualityComparer);
//         byte[] key = new byte[32];
//         byte[] value = new byte[32];
//         DateTime start = DateTime.Now;
//         for (int i = 0; i < NumKeys; i++)
//         {
//             Random.NextBytes(key);
//             Random.NextBytes(value);
//             kvMap[key.AsSpan().ToArray()] = value.AsSpan().ToArray();
//             tree.Insert((Hash256)key, value);
//         }
//         DateTime check1 = DateTime.Now;
//         tree.Commit();
//         tree.CommitTree(block++);
//         DateTime check2 = DateTime.Now;
//         Console.WriteLine($"{block} Insert: {(check1 - start).TotalMilliseconds}");
//         Console.WriteLine($"{block} Flush: {(check2 - check1).TotalMilliseconds}");
//
//         Hash256 requiredStateRoot = Hash256.Zero;
//         SortedSet<byte[]> keys = new(Bytes.Comparer);
//         for (int i = 10; i < NumKeys; i += 10)
//         {
//             DateTime check5 = DateTime.Now;
//             Random.NextBytes(key);
//             Random.NextBytes(value);
//             for (int j = (i-10); j < i; j += 1)
//             {
//                 Random.NextBytes(key);
//                 Random.NextBytes(value);
//                 kvMap[key.AsSpan().ToArray()] = value.AsSpan().ToArray();
//                 tree.Insert((Hash256)key, value);
//                 keys.Add(key.AsSpan().ToArray());
//             }
//             DateTime check3 = DateTime.Now;
//             tree.Commit();
//             if (block == 180) requiredStateRoot = tree.StateRoot;
//             tree.CommitTree(block++);
//             DateTime check4 = DateTime.Now;
//             Console.WriteLine($"{block} Insert: {(check3 - check5).TotalMilliseconds}");
//             Console.WriteLine($"{block} Flush: {(check4 - check3).TotalMilliseconds}");
//         }
//         DateTime check6 = DateTime.Now;
//         Console.WriteLine($"Loop Time: {(check6 - check2).TotalMilliseconds}");
//         Console.WriteLine($"Total Time: {(check6 - start).TotalMilliseconds}");
//
//
//         byte[][] keysArray = keys.ToArray();
//         using IEnumerator<KeyValuePair<byte[], byte[]>> rangeEnum =
//             tree._verkleStateStore.GetLeafRangeIterator(keysArray[30], keysArray[90], requiredStateRoot).GetEnumerator();
//
//         while (rangeEnum.MoveNext())
//         {
//             Console.WriteLine($"Key:{rangeEnum.Current.Key.ToHexString()} Value:{rangeEnum.Current.Value.ToHexString()}");
//             Assert.That(rangeEnum.Current.Value.SequenceEqual(kvMap[rangeEnum.Current.Key]), Is.True);
//         }
//     }
//
//     [TestCase(DbMode.MemDb)]
//     [TestCase(DbMode.PersistantDb)]
//     public void TestRangeIterator(DbMode dbMode)
//     {
//         const int pathPoolCount = 100_000;
//         const int leafPerBlock = 10;
//
//         IVerkleTreeStore store = TestItem.GetVerkleStore<VerkleSyncCache>(dbMode);
//         VerkleTree tree = new(store, LimboLogs.Instance);
//
//         Hash256[] pathPool = GetPedersenPathPool(pathPoolCount);
//         SortedDictionary<Hash256, byte[]> leafs = new();
//
//         for (int leafIndex = 0; leafIndex < 10000; leafIndex++)
//         {
//             byte[] value = new byte[32];
//             Random.NextBytes(value);
//             Hash256 path = pathPool[Random.Next(pathPool.Length - 1)];
//             tree.Insert(path, value);
//             leafs[path] = value;
//             Console.WriteLine($"blockNumber:{0} nKey:{path} nValue:{value.ToHexString()}");
//         }
//
//         tree.Commit();
//         tree.CommitTree(0);
//
//         Hash256 requiredStateRoot = Hash256.Zero;
//         for (int blockNumber = 1; blockNumber <= 180; blockNumber++)
//         {
//             for (int accountIndex = 0; accountIndex < leafPerBlock; accountIndex++)
//             {
//                 byte[] leafValue = new byte[32];
//
//                 Random.NextBytes(leafValue);
//                 Hash256 path = pathPool[Random.Next(pathPool.Length - 1)];
//
//                 if (leafs.ContainsKey(path))
//                 {
//                     if (!(Random.NextSingle() > 0.5)) continue;
//                     Console.WriteLine($"blockNumber:{blockNumber} uKey:{path} uValue:{leafValue.ToHexString()}");
//                     tree.Insert(path, leafValue);
//                     leafs[path] = leafValue;
//                     Console.WriteLine("new values");
//                 }
//                 else
//                 {
//                     Console.WriteLine($"blockNumber:{blockNumber} nKey:{path} nValue:{leafValue.ToHexString()}");
//                     tree.Insert(path, leafValue);
//                     leafs[path] = leafValue;
//                 }
//             }
//
//             tree.Commit();
//             tree.CommitTree(blockNumber);
//             if (blockNumber == 180) requiredStateRoot = tree.StateRoot;
//             {
//
//             }
//         }
//
//         KeyValuePair<byte[], byte[]>[] rangeEnum =
//             tree._verkleStateStore.GetLeafRangeIterator(Hash256.Zero.Bytes.ToArray(), Hash256.MaxValue.Bytes.ToArray(), requiredStateRoot)
//                 .ToArray();
//
//         int index = 0;
//         foreach (KeyValuePair<Hash256, byte[]> leaf in leafs)
//         {
//             Console.WriteLine($"{leaf.Key} {rangeEnum[index].Key.ToHexString()}");
//             Console.WriteLine($"{leaf.Value.ToHexString()} {rangeEnum[index].Value.ToArray()}");
//             Assert.IsTrue(leaf.Key.Bytes.SequenceEqual(rangeEnum[index].Key));
//             Assert.IsTrue(leaf.Value.SequenceEqual(rangeEnum[index].Value));
//             index++;
//         }
//     }
//
//     private class TrieStorePoolPolicy : IPooledObjectPolicy<IVerkleTreeStore>
//     {
//         private readonly IDbProvider _dbProvider;
//         private readonly ILogManager _logManager;
//
//         public TrieStorePoolPolicy(IDbProvider provider, ILogManager logManager)
//         {
//             _dbProvider = provider;
//             _logManager = logManager;
//         }
//
//         public IVerkleTreeStore Create()
//         {
//             return new VerkleTreeStore<PersistEveryBlock>(_dbProvider, _logManager);
//         }
//
//         public bool Return(IVerkleTreeStore obj)
//         {
//             return true;
//         }
//     }
// }
