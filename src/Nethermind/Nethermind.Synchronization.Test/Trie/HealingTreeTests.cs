// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Threading.Tasks;
using Autofac;
using FluentAssertions;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.State.Healing;
using Nethermind.State.Snap;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Trie;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.Trie;

[Parallelizable(ParallelScope.Fixtures)]
public class HealingTreeTests
{
    private static readonly byte[] _rlp = { 3, 4 };
    private static readonly Hash256 _key = Keccak.Compute(_rlp);

    [Test]
    public void get_state_tree_works()
    {
        HealingStateTree stateTree = new(Substitute.For<ITrieStore>(), LimboLogs.Instance);
        stateTree.Get(stackalloc byte[] { 1, 2, 3 });
    }

    [Test]
    public void get_storage_tree_works()
    {
        HealingStorageTree stateTree = new(Substitute.For<IScopedTrieStore>(), Keccak.EmptyTreeHash, LimboLogs.Instance, TestItem.AddressA, TestItem.KeccakA, null);
        stateTree.Get(stackalloc byte[] { 1, 2, 3 });
    }

    [Test]
    public void recovery_works_state_trie([Values(true, false)] bool isMainThread, [Values(true, false)] bool successfullyRecovered)
    {
        static HealingStateTree CreateHealingStateTree(ITrieStore trieStore, ITrieNodeRecovery<GetTrieNodesRequest> recovery)
        {
            HealingStateTree stateTree = new(trieStore, LimboLogs.Instance);
            stateTree.InitializeNetwork(recovery);
            return stateTree;
        }

        byte[] path = { 1, 2 };
        recovery_works(isMainThread, successfullyRecovered, path, CreateHealingStateTree, r => PathMatch(r, path, 0));
    }

    private static bool PathMatch(GetTrieNodesRequest r, byte[] path, int lastPathIndex) =>
        r.RootHash == _key
        && r.AccountAndStoragePaths.Count == 1
        && r.AccountAndStoragePaths[0].Group.Length == lastPathIndex + 1
        && Bytes.AreEqual(r.AccountAndStoragePaths[0].Group[lastPathIndex], Nibbles.EncodePath(path));

    [Test]
    public void recovery_works_storage_trie([Values(true, false)] bool isMainThread, [Values(true, false)] bool successfullyRecovered)
    {
        static HealingStorageTree CreateHealingStorageTree(ITrieStore trieStore, ITrieNodeRecovery<GetTrieNodesRequest> recovery) =>
            new(trieStore.GetTrieStore(null), Keccak.EmptyTreeHash, LimboLogs.Instance, TestItem.AddressA, _key, recovery);
        byte[] path = { 1, 2 };
        byte[] addressPath = ValueKeccak.Compute(TestItem.AddressA.Bytes).Bytes.ToArray();
        recovery_works(isMainThread, successfullyRecovered, path, CreateHealingStorageTree,
            r => PathMatch(r, path, 1) && Bytes.AreEqual(r.AccountAndStoragePaths[0].Group[0], addressPath));
    }

    private void recovery_works<T>(
        bool isMainThread,
        bool successfullyRecovered,
        byte[] path,
        Func<ITrieStore, ITrieNodeRecovery<GetTrieNodesRequest>, T> createTrie,
        Expression<Predicate<GetTrieNodesRequest>> requestMatch)
        where T : PatriciaTree
    {
        ITrieStore trieStore = Substitute.For<ITrieStore>();
        trieStore.FindCachedOrUnknown(null, TreePath.Empty, _key).Returns(
            k => throw new MissingTrieNodeException("", new TrieNodeException("", _key), path, 1),
            k => new TrieNode(NodeType.Leaf) { Key = path });
        trieStore.GetTrieStore(Arg.Any<Hash256?>())
            .Returns((callInfo) => new ScopedTrieStore(trieStore, (Hash256?)callInfo[0]));
        TestMemDb db = new();
        trieStore.TrieNodeRlpStore.Returns(db);

        ITrieNodeRecovery<GetTrieNodesRequest> recovery = Substitute.For<ITrieNodeRecovery<GetTrieNodesRequest>>();
        recovery.CanRecover.Returns(isMainThread);
        recovery.Recover(_key, Arg.Is(requestMatch)).Returns(successfullyRecovered ? Task.FromResult<byte[]?>(_rlp) : Task.FromResult<byte[]?>(null));

        T trie = createTrie(trieStore, recovery);

        Action action = () => trie.Get(stackalloc byte[] { 1, 2, 3 }, _key);
        if (isMainThread && successfullyRecovered)
        {
            action.Should().NotThrow();
            trieStore.Received()
                .Set(null, TreePath.FromNibble(path), ValueKeccak.Compute(_rlp), _rlp);
        }
        else
        {
            action.Should().Throw<MissingTrieNodeException>();
        }
    }

    [TestCase(INodeStorage.KeyScheme.Hash)]
    [TestCase(INodeStorage.KeyScheme.HalfPath)]
    public async Task HealingTreeTest(INodeStorage.KeyScheme keyScheme)
    {
        await using IContainer server = CreateNode();
        await using IContainer client = CreateNode();

        // Add some data to the server.
        Hash256 stateRoot = FillStorage(server);

        RandomCopyState(server, client);

        ISyncPeerPool clientSyncPeerPool = client.Resolve<ISyncPeerPool>();
        clientSyncPeerPool.Start();
        clientSyncPeerPool.AddPeer(server.Resolve<SyncPeerMock>());

        // Make sure that the client have the same data.
        AssertStorage(client);

        IContainer CreateNode()
        {
            ConfigProvider configProvider = new ConfigProvider();
            configProvider.GetConfig<IPruningConfig>().Mode = PruningMode.Full;
            configProvider.GetConfig<IInitConfig>().StateDbKeyScheme = keyScheme;
            return new ContainerBuilder()
                .AddModule(new TestNethermindModule(configProvider))
                .AddSingleton<IBlockTree>(Build.A.BlockTree().OfChainLength(1).TestObject)
                .OnBuild((ctx) =>
                {
                    ILogManager logManager = ctx.Resolve<ILogManager>();
                    ISyncPeerPool peerPool = ctx.Resolve<ISyncPeerPool>();
                    // Always recover flag
                    ctx.Resolve<IWorldStateManager>().InitializeNetwork(
                        new GetNodeDataTrieNodeRecovery(peerPool, logManager, true),
                        new SnapTrieNodeRecovery(peerPool, logManager, true));
                })
                .Build();
        }

        Hash256 FillStorage(IContainer server)
        {
            IWorldState mainWorldState = server.Resolve<MainBlockProcessingContext>().WorldState;
            mainWorldState.StateRoot = Keccak.EmptyTreeHash;

            for (int i = 0; i < 100; i++)
            {
                Address address = new Address(Keccak.Compute(i.ToString()));
                mainWorldState.CreateAccount(address, (UInt256)i, (UInt256)i);
            }

            Address storageAddress = new Address(Keccak.Compute("storage"));
            mainWorldState.CreateAccount(storageAddress, 100, 100);
            for (int i = 1; i < 100; i++)
            {
                mainWorldState.Set(new StorageCell(storageAddress, (UInt256)i), i.ToBigEndianByteArray());
            }

            mainWorldState.Commit(Cancun.Instance);
            mainWorldState.CommitTree(1);

            return mainWorldState.StateRoot;
        }

        void RandomCopyState(IContainer server, IContainer client)
        {
            IDb clientStateDb = client.ResolveNamed<IDb>(DbNames.State);
            IDb serverStateDb = server.ResolveNamed<IDb>(DbNames.State);

            Random random = new Random(0);
            using ArrayPoolList<KeyValuePair<byte[], byte[]?>> allValues = serverStateDb.GetAll().ToPooledList(10);
            // Sort for reproducability
            allValues.AsSpan().Sort(((k1, k2) => ((IComparer<byte[]>)Bytes.Comparer).Compare(k1.Key, k2.Key)));

            // Copy from server to client, but randomly remove some of them.
            foreach (var kv in allValues)
            {
                if (random.NextDouble() < 0.9)
                {
                    clientStateDb[kv.Key] = kv.Value;
                }
            }
        }

        void AssertStorage(IContainer client)
        {
            IWorldState mainWorldState = client.Resolve<MainBlockProcessingContext>().WorldState;
            mainWorldState.StateRoot = stateRoot;

            for (int i = 0; i < 10; i++)
            {
                Address address = new Address(Keccak.Compute(i.ToString()));
                mainWorldState.GetBalance(address).Should().Be((UInt256)i);
                mainWorldState.GetNonce(address).Should().Be((UInt256)i);
            }

            Address storageAddress = new Address(Keccak.Compute("storage"));
            mainWorldState.GetBalance(storageAddress).Should().Be((UInt256)100);
            mainWorldState.GetNonce(storageAddress).Should().Be((UInt256)100);
            for (int i = 1; i < 10; i++)
            {
                mainWorldState.Get(new StorageCell(storageAddress, (UInt256)i)).ToArray().Should().BeEquivalentTo(i.ToBigEndianByteArray());
            }
        }
    }
}
