// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
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
using Nethermind.Evm.State;
using Nethermind.State.Healing;
using Nethermind.Synchronization.Peers;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NSubstitute;
using NUnit.Framework;
using Nethermind.History;
using Nethermind.Init.Modules;

namespace Nethermind.Synchronization.Test.Trie;

[Parallelizable(ParallelScope.Fixtures)]
public class HealingTreeTests
{
    private static readonly byte[] _rlp = { 3, 4 };
    private static readonly Hash256 _key = Keccak.Compute(_rlp);
    private static readonly byte[] _code = { 0x60, 0x00, 0x60, 0x00, 0xf3 };
    private static readonly Address _codeAddress = new(Keccak.Compute("code"));

    [Test]
    public void get_state_tree_works()
    {
        HealingStateTree stateTree = new(Substitute.For<ITrieStore>(), Substitute.For<INodeStorage>(), new Lazy<IPathRecovery>(), LimboLogs.Instance);
        stateTree.Get(stackalloc byte[] { 1, 2, 3 });
    }

    [Test]
    public void get_storage_tree_works()
    {
        HealingStorageTree stateTree = new(Substitute.For<IScopedTrieStore>(), Substitute.For<INodeStorage>(), Keccak.EmptyTreeHash, LimboLogs.Instance, TestItem.AddressA, TestItem.KeccakA, new Lazy<IPathRecovery>());
        stateTree.Get(stackalloc byte[] { 1, 2, 3 });
    }

    [Test]
    public void recovery_works_state_trie([Values(true, false)] bool successfullyRecovered)
    {
        static HealingStateTree CreateHealingStateTree(ITrieStore trieStore, INodeStorage nodeStorage, IPathRecovery recovery)
        {
            HealingStateTree stateTree = new(trieStore, nodeStorage, new Lazy<IPathRecovery>(recovery), LimboLogs.Instance);
            return stateTree;
        }

        TreePath path = TreePath.FromNibble([1, 2]);
        Hash256 fullPath = new("1200000000000000000000000000000000000000000000000000000000000000");
        recovery_works(successfullyRecovered, null, path, fullPath, CreateHealingStateTree);
    }

    [Test]
    public void recovery_works_storage_trie([Values(true, false)] bool successfullyRecovered)
    {
        Hash256 addressPath = Keccak.Compute(TestItem.AddressA.Bytes);
        HealingStorageTree CreateHealingStorageTree(ITrieStore trieStore, INodeStorage nodeStorage, IPathRecovery recovery) =>
            new(trieStore.GetTrieStore(addressPath), nodeStorage, Keccak.EmptyTreeHash, LimboLogs.Instance, TestItem.AddressA,
                _key, new Lazy<IPathRecovery>(recovery));

        TreePath path = TreePath.FromNibble([1, 2]);
        Hash256 fullPath = new("1200000000000000000000000000000000000000000000000000000000000000");

        recovery_works(successfullyRecovered, addressPath, path, fullPath, CreateHealingStorageTree);
    }

    [Test]
    public void code_recovery_works([Values] bool successfullyRecovered)
    {
        using TestMemDb db = new();
        (HealingCodeDb codeDb, ICodeRecovery recovery) = HealingCodeDbOver(db);
        recovery.Recover(_key.ValueHash256, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(successfullyRecovered ? _rlp : null));

        Assert.That(codeDb.Get(_key.Bytes), Is.EqualTo(successfullyRecovered ? _rlp : null));
        if (successfullyRecovered)
        {
            // Recovered code is persisted, so the next read no longer hits the network.
            Assert.That(db[_key.Bytes], Is.EqualTo(_rlp));
            Assert.That(codeDb.Get(_key.Bytes), Is.EqualTo(_rlp));
            recovery.Received(1).Recover(_key.ValueHash256, Arg.Any<CancellationToken>());
        }
    }

    [Test]
    public void code_recovery_collapses_concurrent_misses()
    {
        // The gate holds the first reader inside the recovery until the second has provably joined it, so
        // the two are overlapping when the single request resolves.
        TaskCompletionSource<byte[]?> gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int reads = 0;
        int recoveries = 0;

        using TestMemDb db = new() { ReadFunc = _ => { Interlocked.Increment(ref reads); return null!; } };
        (HealingCodeDb codeDb, ICodeRecovery recovery) = HealingCodeDbOver(db);
        recovery.Recover(_key.ValueHash256, Arg.Any<CancellationToken>())
            .Returns(_ => { Interlocked.Increment(ref recoveries); return gate.Task; });

        // Dedicated threads, not the pool: both readers park on the recovery, and a saturated pool
        // could otherwise leave the second one queued behind the first.
        (Thread _, Task<byte[]?> first) = ReadOnOwnThread(codeDb);
        Assert.That(() => Volatile.Read(ref recoveries), Is.EqualTo(1).After(10000, 10));

        (Thread secondThread, Task<byte[]?> second) = ReadOnOwnThread(codeDb);
        // Releasing the gate on the second db read alone would race the join: the first reader could
        // finish and drop the shared entry before the second reaches it, so the second would start its
        // own recovery and the count below would legitimately read 2. Once that read has happened the
        // shared recovery is the only thing left for the second reader to block on, so waiting for it to
        // park there is what proves it joined.
        Assert.That(() => Volatile.Read(ref reads), Is.EqualTo(2).After(10000, 10));
        Assert.That(() => secondThread.ThreadState.HasFlag(ThreadState.WaitSleepJoin), Is.True.After(10000, 10));

        gate.SetResult(_rlp);

        Assert.That(Task.WhenAll(first, second).Wait(TimeSpan.FromSeconds(30)), Is.True);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(first.Result, Is.EqualTo(_rlp), "first reader");
            Assert.That(second.Result, Is.EqualTo(_rlp), "second reader");
            Assert.That(recoveries, Is.EqualTo(1), "recoveries");
        }

        static (Thread, Task<byte[]?>) ReadOnOwnThread(HealingCodeDb codeDb)
        {
            TaskCompletionSource<byte[]?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Thread thread = new(() =>
            {
                try
                {
                    completion.SetResult(codeDb.Get(_key.Bytes));
                }
                catch (Exception e)
                {
                    completion.SetException(e);
                }
            })
            { IsBackground = true };
            thread.Start();
            return (thread, completion.Task);
        }
    }

    /// <summary>The read members of <see cref="IKeyValueStoreWithBatching"/> a code DB can be asked through.</summary>
    public enum CodeRead { Get, Indexer, GetSpan, GetIntoSpan, GetOwnedMemory, KeyExists }

    [Test]
    public void code_recovery_reaches_every_read_member([Values] CodeRead read)
    {
        using TestMemDb db = new();
        (HealingCodeDb codeDb, ICodeRecovery recovery) = HealingCodeDbOver(db);
        recovery.Recover(_key.ValueHash256, Arg.Any<CancellationToken>()).Returns(Task.FromResult<byte[]?>(_rlp));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ReadThrough(codeDb, db, read), Is.EqualTo(_rlp), "recovered");
            Assert.That(ReadThrough(codeDb, db, read), Is.EqualTo(_rlp), "served locally afterwards");
        }

        recovery.Received(1).Recover(_key.ValueHash256, Arg.Any<CancellationToken>());
    }

    [Test]
    public void code_key_exists_that_hits_skips_the_allocating_get()
    {
        using TestMemDb db = new() { [_key.Bytes] = _rlp };
        (HealingCodeDb codeDb, ICodeRecovery recovery) = HealingCodeDbOver(db);

        Assert.That(codeDb.KeyExists(_key.Bytes), Is.True);

        // MemDb answers KeyExists from its own map, while the `byte[] Get` this would otherwise fall
        // back through is the only member TestMemDb records - so no recorded read is the proof.
        db.KeyWasRead(_key.BytesToArray(), times: 0);
        recovery.DidNotReceiveWithAnyArgs().Recover(default, default);
    }

    /// <summary>Wraps <paramref name="db"/> in the healing code db, behind a recovery each test stubs itself.</summary>
    private static (HealingCodeDb CodeDb, ICodeRecovery Recovery) HealingCodeDbOver(TestMemDb db)
    {
        ICodeRecovery recovery = Substitute.For<ICodeRecovery>();
        return (new HealingCodeDb(db, new Lazy<ICodeRecovery>(recovery)), recovery);
    }

    private static byte[]? ReadThrough(HealingCodeDb codeDb, TestMemDb db, CodeRead read) => read switch
    {
        CodeRead.Get => codeDb.Get(_key.Bytes),
        CodeRead.Indexer => ((IKeyValueStore)codeDb)[_key.Bytes],
        CodeRead.GetSpan => ReadSpan(codeDb),
        CodeRead.GetIntoSpan => ReadIntoSpan(codeDb),
        CodeRead.GetOwnedMemory => ReadOwnedMemory(codeDb),
        CodeRead.KeyExists => codeDb.KeyExists(_key.Bytes) ? db[_key.Bytes] : null,
        _ => throw new ArgumentOutOfRangeException(nameof(read), read, null)
    };

    private static byte[]? ReadSpan(HealingCodeDb codeDb)
    {
        Span<byte> span = codeDb.GetSpan(_key.Bytes);
        try
        {
            return span.IsNull() ? null : span.ToArray();
        }
        finally
        {
            codeDb.DangerousReleaseMemory(span);
        }
    }

    private static byte[]? ReadIntoSpan(HealingCodeDb codeDb)
    {
        byte[] output = new byte[_rlp.Length];
        int length = codeDb.Get(_key.Bytes, output);
        return length == 0 ? null : output[..length];
    }

    private static byte[]? ReadOwnedMemory(HealingCodeDb codeDb)
    {
        using MemoryManager<byte>? memory = codeDb.GetOwnedMemory(_key.Bytes);
        return memory?.Memory.ToArray();
    }

    [Test]
    public void code_recovery_skips_present_code()
    {
        using TestMemDb db = new() { [_key.Bytes] = _rlp };
        (HealingCodeDb codeDb, ICodeRecovery recovery) = HealingCodeDbOver(db);

        Assert.That(codeDb.Get(_key.Bytes), Is.EqualTo(_rlp));
        recovery.DidNotReceiveWithAnyArgs().Recover(default, default);
    }

    [Test]
    public void code_recovery_skips_keys_that_cannot_be_a_code_hash()
    {
        using TestMemDb db = new();
        (HealingCodeDb codeDb, ICodeRecovery recovery) = HealingCodeDbOver(db);

        Assert.That(codeDb.Get([1, 2, 3]), Is.Null);
        recovery.DidNotReceiveWithAnyArgs().Recover(default, default);
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
            Assert.That(action, Throws.Nothing);
            db.KeyWasWritten(NodeStorage.GetHalfPathNodeStoragePath(address, path, ValueKeccak.Compute(_rlp)));
        }
        else
        {
            Assert.That(action, Throws.TypeOf<MissingTrieNodeException>());
        }
    }

    [Test]
    public async Task HealingTreeTest([Values(INodeStorage.KeyScheme.Hash, INodeStorage.KeyScheme.HalfPath)] INodeStorage.KeyScheme keyScheme)
    {
        await using IContainer server = CreateNode();
        await using IContainer client = CreateNode();

        // Add some data to the server.
        BlockHeader baseBlock = FillStorage(server);

        RandomCopyState(server, client);

        ISyncPeerPool clientSyncPeerPool = client.Resolve<ISyncPeerPool>();
        clientSyncPeerPool.Start();
        clientSyncPeerPool.AddPeer(server.Resolve<SyncPeerMock>());

        // Make sure that the client have the same data.
        AssertStorage(client);

        IContainer CreateNode()
        {
            ConfigProvider configProvider = new();
            // Trie node healing is a patricia state-sync repair mechanism with no flat equivalent.
            configProvider.GetConfig<IFlatDbConfig>().Enabled = false;
            configProvider.GetConfig<IPruningConfig>().Mode = PruningMode.Full;
            configProvider.GetConfig<IInitConfig>().StateDbKeyScheme = keyScheme;
            return new ContainerBuilder()
                .AddModule(new TestNethermindModule(configProvider))
                .AddSingleton<IHistoryPruner>(Substitute.For<IHistoryPruner>())
                .AddSingleton<IBlockTree>(Build.A.BlockTree().OfChainLength(1).TestObject)
                .Build();
        }

        BlockHeader FillStorage(IContainer server)
        {
            IWorldState mainWorldState = server.Resolve<MainProcessingContext>().WorldState;
            IBlockTree blockTree = server.Resolve<IBlockTree>();

            using IDisposable _ = mainWorldState.BeginScope(blockTree.Head?.Header);

            for (ulong i = 0; i < 100; i++)
            {
                Address address = new(Keccak.Compute(i.ToString()));
                mainWorldState.CreateAccount(address, (UInt256)i, i);
            }

            Address storageAddress = new(Keccak.Compute("storage"));
            mainWorldState.CreateAccount(storageAddress, 100, 100);
            for (int i = 1; i < 100; i++)
            {
                mainWorldState.Set(new StorageCell(storageAddress, (UInt256)i), new UInt256(i.ToBigEndianByteArray(), isBigEndian: true));
            }

            mainWorldState.CreateAccount(_codeAddress, 1, 1);
            mainWorldState.InsertCode(_codeAddress, ValueKeccak.Compute(_code), _code, Cancun.Instance);

            mainWorldState.Commit(Cancun.Instance);

            // Snap server check for the past 128 block in blocktree explicitly to pass hive test.
            // So need to simulate block processing..
            mainWorldState.CommitTree((blockTree.Head?.Number ?? 0) + 1);

            Block block = Build.A.Block.WithStateRoot(mainWorldState.StateRoot).WithParent(blockTree.Head!).TestObject;

            Assert.That(blockTree.SuggestBlock(block), Is.EqualTo(AddBlockResult.Added));
            blockTree.TryUpdateMainChain(block.Header, true, preloadedBlocks: new[] { block });

            return block.Header;
        }

        void RandomCopyState(IContainer server, IContainer client)
        {
            IDb clientStateDb = client.ResolveNamed<IDb>(DbNames.State);
            IDb serverStateDb = server.ResolveNamed<IDb>(DbNames.State);

            Random random = new(0);
            using ArrayPoolList<KeyValuePair<byte[], byte[]>> allValues = serverStateDb.GetAll().ToPooledList(10);
            // Sort for reproducibility
            allValues.AsSpan().Sort(((k1, k2) => ((IComparer<byte[]>)Bytes.Comparer).Compare(k1.Key, k2.Key)));

            // Copy from server to client, but randomly remove some of them.
            foreach (KeyValuePair<byte[], byte[]> kv in allValues.AsSpan())
            {
                if (random.NextDouble() < 0.9)
                {
                    clientStateDb[kv.Key] = kv.Value;
                }
            }
        }

        void AssertStorage(IContainer client)
        {
            IWorldState mainWorldState = client.Resolve<MainProcessingContext>().WorldState;
            using IDisposable _ = mainWorldState.BeginScope(baseBlock);

            for (ulong i = 0; i < 100; i++)
            {
                Address address = new(Keccak.Compute(i.ToString()));
                Assert.That(mainWorldState.GetBalance(address), Is.EqualTo((UInt256)i));
                Assert.That(mainWorldState.GetNonce(address), Is.EqualTo(i));
            }

            Address storageAddress = new(Keccak.Compute("storage"));
            Assert.That(mainWorldState.GetBalance(storageAddress), Is.EqualTo((UInt256)100));
            Assert.That(mainWorldState.GetNonce(storageAddress), Is.EqualTo(100ul));
            for (int i = 1; i < 100; i++)
            {
                mainWorldState.Get(new StorageCell(storageAddress, (UInt256)i), out UInt256 storageValue1);
                Assert.That(storageValue1, Is.EqualTo((UInt256)i));
            }

            if (keyScheme == INodeStorage.KeyScheme.HalfPath)
            {
                Assert.That(mainWorldState.GetCode(_codeAddress), Is.EqualTo(_code));
            }
        }
    }
}
