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
using Nethermind.Synchronization.Peers;
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
        HealingStateTree stateTree = new(Substitute.For<ITrieStore>(), Substitute.For<INodeStorage>(), LimboLogs.Instance);
        stateTree.Get(stackalloc byte[] { 1, 2, 3 });
    }

    [Test]
    public void get_storage_tree_works()
    {
        HealingStorageTree stateTree = new(Substitute.For<IScopedTrieStore>(), Substitute.For<INodeStorage>(), Keccak.EmptyTreeHash, LimboLogs.Instance, TestItem.AddressA, TestItem.KeccakA, null);
        stateTree.Get(stackalloc byte[] { 1, 2, 3 });
    }

    [Test]
    public void recovery_works_state_trie([Values(true, false)] bool successfullyRecovered)
    {
        static HealingStateTree CreateHealingStateTree(ITrieStore trieStore, INodeStorage nodeStorage, IPathRecovery recovery)
        {
            HealingStateTree stateTree = new(trieStore, nodeStorage, LimboLogs.Instance);
            stateTree.InitializeNetwork(recovery);
            return stateTree;
        }

        TreePath path = TreePath.FromNibble([1, 2]);
        Hash256 fullPath = new Hash256("1200000000000000000000000000000000000000000000000000000000000000");
        recovery_works(successfullyRecovered, null, path, fullPath, CreateHealingStateTree);
    }

    [Test]
    public void recovery_works_storage_trie([Values(true, false)] bool successfullyRecovered)
    {
        Hash256 addressPath = Keccak.Compute(TestItem.AddressA.Bytes);
        HealingStorageTree CreateHealingStorageTree(ITrieStore trieStore, INodeStorage nodeStorage, IPathRecovery recovery) =>
            new(trieStore.GetTrieStore(addressPath), nodeStorage, Keccak.EmptyTreeHash, LimboLogs.Instance, TestItem.AddressA,
                _key, recovery);

        TreePath path = TreePath.FromNibble([1, 2]);
        Hash256 fullPath = new Hash256("1200000000000000000000000000000000000000000000000000000000000000");

        recovery_works(successfullyRecovered, addressPath, path, fullPath, CreateHealingStorageTree);
    }

    private void recovery_works<T>(
        bool successfullyRecovered,
        Hash256? address,
        TreePath path,
        Hash256 fullPath,
        Func<ITrieStore, INodeStorage, IPathRecovery, T> createTrie)
        where T : PatriciaTree
    {
        IPruningTrieStore trieStore = Substitute.For<IPruningTrieStore>();
        trieStore.FindCachedOrUnknown(address, TreePath.Empty, _key).Returns(
            k => throw new MissingTrieNodeException("", null, path, _key),
            k => new TrieNode(NodeType.Leaf) { Key = Nibbles.BytesToNibbleBytes(fullPath.Bytes)[path.Length..] });
        trieStore.GetTrieStore(Arg.Is<Hash256?>(address))
            .Returns((callInfo) => new ScopedTrieStore(trieStore, (Hash256?)callInfo[0]));
        TestMemDb db = new();
        trieStore.TrieNodeRlpStore.Returns(db);

        IPathRecovery recovery = Substitute.For<IPathRecovery>();
        recovery.Recover(Arg.Any<Hash256>(), Arg.Is<Hash256?>(address), Arg.Is<TreePath>(path), _key, fullPath)
            .Returns(successfullyRecovered ? Task.FromResult<IOwnedReadOnlyList<(TreePath, byte[])>?>(
                new ArrayPoolList<(TreePath, byte[])>(1)
                {
                    { (path, _rlp) }
                }
            ) : Task.FromResult<IOwnedReadOnlyList<(TreePath, byte[])>?>(null));

        T trie = createTrie(trieStore, new NodeStorage(db), recovery);

        Action action = () => trie.Get(fullPath.Bytes, _key);
        if (successfullyRecovered)
        {
            action.Should().NotThrow();
            db.KeyWasWritten(NodeStorage.GetHalfPathNodeStoragePath(address, path, ValueKeccak.Compute(_rlp)));
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
                .Build();
        }

        Hash256 FillStorage(IContainer server)
        {
            IWorldState mainWorldState = server.Resolve<MainBlockProcessingContext>().WorldState;
            IBlockTree blockTree = server.Resolve<IBlockTree>();
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

            // Snap server check for the past 128 block in blocktree explicitly to pass hive test.
            // So need to simulate block processing..
            Block block = Build.A.Block.WithStateRoot(mainWorldState.StateRoot).WithParent(blockTree.Head!).TestObject;
            blockTree.SuggestBlock(block).Should().Be(AddBlockResult.Added);
            blockTree.UpdateMainChain([block], true);

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
            foreach (var kv in allValues.AsSpan())
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

            for (int i = 0; i < 100; i++)
            {
                Address address = new Address(Keccak.Compute(i.ToString()));
                mainWorldState.GetBalance(address).Should().Be((UInt256)i);
                mainWorldState.GetNonce(address).Should().Be((UInt256)i);
            }

            Address storageAddress = new Address(Keccak.Compute("storage"));
            mainWorldState.GetBalance(storageAddress).Should().Be((UInt256)100);
            mainWorldState.GetNonce(storageAddress).Should().Be((UInt256)100);
            for (int i = 1; i < 100; i++)
            {
                mainWorldState.Get(new StorageCell(storageAddress, (UInt256)i)).ToArray().Should().BeEquivalentTo(i.ToBigEndianByteArray());
            }
        }
    }
}
