using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Threading;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using Nethermind.State.Snap;
using Nethermind.State.SnapServer;
using Nethermind.Synchronization.FastSync;
using Nethermind.Synchronization.SnapSync;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.SnapSync;

[TestFixture]
public class SnapServerTest
{
    internal interface IWriteBatch : IDisposable
    {
        void SetAccount(Address address, Account account);
        void SetAccount(Hash256 accountPath, Account account);
        void SetSlot(Hash256 storagePath, ValueHash256 slotKey, byte[] value, bool rlpEncode = true);
    }

    internal interface ISnapServerContext : IDisposable
    {
        ISnapServer Server { get; }
        SnapProvider SnapProvider { get; }
        Hash256 RootHash { get; }
        int PersistedNodeCount { get; }

        IWriteBatch BeginWriteBatch();
        Hash256 GetStorageRoot(Hash256 storagePath);
    }

    private class TrieSnapServerContext : ISnapServerContext
    {
        private readonly TestRawTrieStore _store;
        private readonly StateTree _tree;
        private readonly MemDb _clientStateDb;

        public ISnapServer Server { get; }
        public SnapProvider SnapProvider { get; }
        public Hash256 RootHash => _tree.RootHash;
        public int PersistedNodeCount => _clientStateDb.Keys.Count;

        internal TrieSnapServerContext(ILastNStateRootTracker? lastNStateRootTracker = null)
        {
            MemDb stateDbServer = new();
            MemDb codeDbServer = new();
            _store = new TestRawTrieStore(stateDbServer);
            _tree = new StateTree(_store, LimboLogs.Instance);
            Server = new SnapServer(_store.AsReadOnly(), codeDbServer, LimboLogs.Instance, lastNStateRootTracker);

            _clientStateDb = new MemDb();
            using ProgressTracker progressTracker = new(_clientStateDb, new TestSyncConfig(), new StateSyncPivot(null!, new TestSyncConfig(), LimboLogs.Instance), LimboLogs.Instance);
            INodeStorage nodeStorage = new NodeStorage(_clientStateDb);
            SnapProvider = new SnapProvider(progressTracker, new MemDb(), new PatriciaSnapTrieFactory(nodeStorage, LimboLogs.Instance), LimboLogs.Instance);
        }

        public IWriteBatch BeginWriteBatch() => new WriteBatch(this);
        public Hash256 GetStorageRoot(Hash256 accountPath) => _tree.Get(accountPath)!.StorageRoot;
        public void Dispose() => ((IDisposable)_store).Dispose();

        private class WriteBatch(TrieSnapServerContext ctx) : IWriteBatch
        {
            private readonly List<(Hash256 Path, Account Account)> _pendingAccounts = new();
            private readonly Dictionary<Hash256, StorageTree> _storageTrees = new();

            public void SetAccount(Address address, Account account) =>
                _pendingAccounts.Add((address.ToAccountPath.ToCommitment(), account));

            public void SetAccount(Hash256 accountPath, Account account) =>
                _pendingAccounts.Add((accountPath, account));

            public void SetSlot(Hash256 storagePath, ValueHash256 slotKey, byte[] value, bool rlpEncode = true)
            {
                if (!_storageTrees.TryGetValue(storagePath, out StorageTree? st))
                {
                    st = new StorageTree(ctx._store.GetTrieStore(storagePath), LimboLogs.Instance);
                    _storageTrees[storagePath] = st;
                }
                st.Set(slotKey, value, rlpEncode);
            }

            public void Dispose()
            {
                Dictionary<Hash256, Hash256> storageRoots = new();
                foreach (var (path, st) in _storageTrees)
                {
                    st.Commit();
                    storageRoots[path] = st.RootHash;
                }

                foreach (var (path, account) in _pendingAccounts)
                {
                    Account finalAccount = storageRoots.TryGetValue(path, out Hash256? root)
                        ? account.WithChangedStorageRoot(root)
                        : account;
                    ctx._tree.Set(path, finalAccount);
                }

                ctx._tree.Commit();
            }
        }
    }

    private ISnapServerContext CreateContext(ILastNStateRootTracker? lastNStateRootTracker = null) =>
        new TrieSnapServerContext(lastNStateRootTracker);

    private static void FillWithTestAccounts(ISnapServerContext context)
    {
        using var batch = context.BeginWriteBatch();
        foreach (var pwa in TestItem.Tree.AccountsWithPaths)
            batch.SetAccount(pwa.Path.ToCommitment(), pwa.Account);
    }

    private static void FillMultipleAccounts(ISnapServerContext context, int count)
    {
        using var batch = context.BeginWriteBatch();
        for (int i = 0; i < count; i++)
            batch.SetAccount(Keccak.Compute(i.ToBigEndianByteArray()), Build.An.Account.WithBalance((UInt256)i).TestObject);
    }

    private static Hash256 FillAccountWithDefaultStorage(ISnapServerContext context)
    {
        using (var batch = context.BeginWriteBatch())
        {
            for (int i = 0; i < 6; i++)
                batch.SetSlot(TestItem.Tree.AccountAddress0, TestItem.Tree.SlotsWithPaths[i].Path, TestItem.Tree.SlotsWithPaths[i].SlotRlpValue, rlpEncode: false);
            batch.SetAccount(TestItem.Tree.AccountAddress0, Build.An.Account.WithBalance(1).TestObject);
        }
        return context.GetStorageRoot(TestItem.Tree.AccountAddress0);
    }

    private static Hash256 FillAccountWithStorage(ISnapServerContext context, int slotCount)
    {
        using (var batch = context.BeginWriteBatch())
        {
            for (int i = 0; i < slotCount; i++)
            {
                var key = Keccak.Compute(i.ToBigEndianByteArray());
                batch.SetSlot(TestItem.Tree.AccountAddress0, key, key.BytesToArray(), rlpEncode: false);
            }
            batch.SetAccount(TestItem.Tree.AccountAddress0, Build.An.Account.WithBalance(1).TestObject);
        }
        return context.GetStorageRoot(TestItem.Tree.AccountAddress0);
    }

    [Test]
    public void TestGetAccountRange()
    {
        using var context = CreateContext();
        FillWithTestAccounts(context);

        (IOwnedReadOnlyList<PathWithAccount> accounts, IByteArrayList proofs) =
            context.Server.GetAccountRanges(context.RootHash, Keccak.Zero, Keccak.MaxValue, 4000, CancellationToken.None);

        AddRangeResult result = context.SnapProvider.AddAccountRange(1, context.RootHash, Keccak.Zero,
            accounts.ToArray(), proofs);

        result.Should().Be(AddRangeResult.OK);
        context.PersistedNodeCount.Should().Be(10);
        accounts.Dispose();
        proofs.Dispose();
    }

    [Test]
    public void TestGetAccountRange_InvalidRange()
    {
        using var context = CreateContext();
        FillWithTestAccounts(context);

        (IOwnedReadOnlyList<PathWithAccount> accounts, IByteArrayList proofs) =
            context.Server.GetAccountRanges(context.RootHash, Keccak.MaxValue, Keccak.Zero, 4000, CancellationToken.None);

        accounts.Count.Should().Be(0);
        accounts.Dispose();
        proofs.Dispose();
    }

    [Test]
    public void TestGetTrieNode_Root()
    {
        using var context = CreateContext();
        FillWithTestAccounts(context);

        using RlpItemList pathSet = PathGroup.EncodeToRlpItemList([
            new PathGroup()
            {
                Group = [[]]
            }
        ]);
        using RlpByteArrayList result = context.Server.GetTrieNodes(pathSet, context.RootHash, default)!;

        result.Count.Should().Be(1);
    }

    [Test]
    public void TestGetTrieNode_Storage_Root()
    {
        using var context = CreateContext();
        FillWithTestAccounts(context);

        using RlpItemList pathSet = PathGroup.EncodeToRlpItemList([
            new PathGroup()
            {
                Group = [TestItem.Tree.AccountsWithPaths[0].Path.Bytes.ToArray(), []]
            }
        ]);
        using RlpByteArrayList result = context.Server.GetTrieNodes(pathSet, context.RootHash, default)!;

        result.Count.Should().Be(1);
    }

    [Test]
    public void TestGetTrieNodes_RespectsHardResponseByteLimit()
    {
        using var context = CreateContext();
        FillMultipleAccounts(context, 1000);

        int requestCount = 5000;
        PathGroup[] groups = new PathGroup[requestCount];
        for (int i = 0; i < requestCount; i++)
            groups[i] = new PathGroup { Group = [[]] };

        using RlpItemList pathSet = PathGroup.EncodeToRlpItemList(groups);
        using RlpByteArrayList result = context.Server.GetTrieNodes(pathSet, context.RootHash, default)!;

        result.Count.Should().BeLessThan(requestCount);
    }

    [TestCase(true)]
    [TestCase(false)]
    public void TestNoState(bool withLastNStateTracker)
    {
        ILastNStateRootTracker? lastNStateTracker = null;
        if (withLastNStateTracker)
        {
            lastNStateTracker = Substitute.For<ILastNStateRootTracker>();
            lastNStateTracker.HasStateRoot(Arg.Any<Hash256>()).Returns(false);
        }

        using var context = CreateContext(lastNStateRootTracker: lastNStateTracker);

        (IOwnedReadOnlyList<PathWithAccount> accounts, IByteArrayList accountProofs) =
            context.Server.GetAccountRanges(context.RootHash, Keccak.Zero, Keccak.MaxValue, 4000, CancellationToken.None);

        accounts.Count.Should().Be(0);

        (IOwnedReadOnlyList<IOwnedReadOnlyList<PathWithStorageSlot>> storageSlots, IByteArrayList? proofs) =
            context.Server.GetStorageRanges(context.RootHash, [TestItem.Tree.AccountsWithPaths[0]],
                ValueKeccak.Zero, ValueKeccak.MaxValue, 10, CancellationToken.None);

        storageSlots.Count.Should().Be(0);

        accounts.Dispose();
        accountProofs.Dispose();
        proofs?.Dispose();
        storageSlots.DisposeRecursive();
    }

    [Test]
    public void TestGetAccountRangeMultiple()
    {
        using var context = CreateContext();
        FillWithTestAccounts(context);

        Hash256 startRange = Keccak.Zero;
        while (true)
        {
            (IOwnedReadOnlyList<PathWithAccount> accounts, IByteArrayList proofs) =
                context.Server.GetAccountRanges(context.RootHash, startRange, Keccak.MaxValue, 100, CancellationToken.None);

            try
            {
                AddRangeResult result = context.SnapProvider.AddAccountRange(1, context.RootHash, startRange,
                    accounts, proofs);

                result.Should().Be(AddRangeResult.OK);
                startRange = accounts[^1].Path.ToCommitment();
                if (startRange.Bytes.SequenceEqual(TestItem.Tree.AccountsWithPaths[^1].Path.Bytes))
                {
                    break;
                }
            }
            finally
            {
                accounts.Dispose();
                proofs.Dispose();
            }
        }
        context.PersistedNodeCount.Should().Be(10);
    }

    [TestCase(10, 10)]
    [TestCase(10000, 1000)]
    [TestCase(10000, 10000000)]
    [TestCase(10000, 10000)]
    public void TestGetAccountRangeMultipleLarger(int stateSize, int byteLimit)
    {
        using var context = CreateContext();
        FillMultipleAccounts(context, stateSize);

        Hash256 startRange = Keccak.Zero;
        while (true)
        {
            (IOwnedReadOnlyList<PathWithAccount> accounts, IByteArrayList proofs) =
                context.Server.GetAccountRanges(context.RootHash, startRange, Keccak.MaxValue, byteLimit, CancellationToken.None);

            try
            {
                AddRangeResult result = context.SnapProvider.AddAccountRange(1, context.RootHash, startRange,
                    accounts, proofs);

                result.Should().Be(AddRangeResult.OK);
                if (startRange == accounts[^1].Path.ToCommitment())
                {
                    break;
                }

                startRange = accounts[^1].Path.ToCommitment();
            }
            finally
            {
                accounts.Dispose();
                proofs.Dispose();
            }
        }
    }

    [TestCase(10, 10)]
    [TestCase(10000, 10)]
    [TestCase(100, 100)]
    [TestCase(10000, 10000000)]
    public void TestGetAccountRangeArtificialLimit(int stateSize, int byteLimit)
    {
        using var context = CreateContext();
        FillMultipleAccounts(context, stateSize);
        Hash256 startRange = Keccak.Zero;

        ValueHash256 limit = new ValueHash256("0x8000000000000000000000000000000000000000000000000000000000000000");
        while (true)
        {
            (IOwnedReadOnlyList<PathWithAccount> accounts, IByteArrayList proofs) = context.Server
                .GetAccountRanges(context.RootHash, startRange, limit, byteLimit, CancellationToken.None);

            try
            {
                AddRangeResult result = context.SnapProvider.AddAccountRange(1, context.RootHash, startRange,
                    accounts, proofs);

                result.Should().Be(AddRangeResult.OK);
                if (startRange == accounts[^1].Path.ToCommitment())
                {
                    break;
                }

                startRange = accounts[^1].Path.ToCommitment();
            }
            finally
            {
                accounts.Dispose();
                proofs.Dispose();
            }
        }
    }

    [Test]
    public void TestGetStorageRange()
    {
        using var context = CreateContext();
        Hash256 storageRoot = FillAccountWithDefaultStorage(context);

        (IOwnedReadOnlyList<IOwnedReadOnlyList<PathWithStorageSlot>> storageSlots, IByteArrayList? proofs) =
            context.Server.GetStorageRanges(context.RootHash, [TestItem.Tree.AccountsWithPaths[0]],
                ValueKeccak.Zero, ValueKeccak.MaxValue, 10, CancellationToken.None);

        try
        {
            var storageRangeRequest = new StorageRange()
            {
                StartingHash = Keccak.Zero,
                Accounts = new ArrayPoolList<PathWithAccount>(1) { new(TestItem.Tree.AccountsWithPaths[0].Path, new Account(UInt256.Zero).WithChangedStorageRoot(storageRoot)) }
            };
            AddRangeResult result = context.SnapProvider.AddStorageRangeForAccount(storageRangeRequest, 0, storageSlots[0], proofs);

            result.Should().Be(AddRangeResult.OK);
        }
        finally
        {
            storageSlots.DisposeRecursive();
            proofs?.Dispose();
        }
    }

    [Test]
    public void TestGetStorageRange_NoSlotsForAccount()
    {
        using var context = CreateContext();
        FillAccountWithDefaultStorage(context);

        ValueHash256 lastStorageHash = TestItem.Tree.SlotsWithPaths[^1].Path;
        var asInt = lastStorageHash.ToUInt256();
        ValueHash256 beyondLast = new ValueHash256((++asInt).ToBigEndian());

        (IOwnedReadOnlyList<IOwnedReadOnlyList<PathWithStorageSlot>> storageSlots, IByteArrayList? proofs) =
            context.Server.GetStorageRanges(context.RootHash, [TestItem.Tree.AccountsWithPaths[0]],
                beyondLast, ValueKeccak.MaxValue, 10, CancellationToken.None);

        storageSlots.Count.Should().Be(0);
        proofs?.Count.Should().BeGreaterThan(0); //in worst case should get at least root node

        storageSlots.DisposeRecursive();
        proofs?.Dispose();
    }

    [Test]
    public void TestGetStorageRangeMulti()
    {
        using var context = CreateContext();
        Hash256 storageRoot = FillAccountWithStorage(context, 10000);

        Hash256 startRange = Keccak.Zero;
        while (true)
        {
            (IOwnedReadOnlyList<IOwnedReadOnlyList<PathWithStorageSlot>> storageSlots, IByteArrayList? proofs) =
                context.Server.GetStorageRanges(context.RootHash, [TestItem.Tree.AccountsWithPaths[0]],
                    startRange, ValueKeccak.MaxValue, 10000, CancellationToken.None);

            try
            {
                var storageRangeRequest = new StorageRange()
                {
                    StartingHash = startRange,
                    Accounts = new ArrayPoolList<PathWithAccount>(1) { new(TestItem.Tree.AccountsWithPaths[0].Path, new Account(UInt256.Zero).WithChangedStorageRoot(storageRoot)) }
                };
                AddRangeResult result = context.SnapProvider.AddStorageRangeForAccount(storageRangeRequest, 0, storageSlots[0], proofs);

                result.Should().Be(AddRangeResult.OK);
                if (startRange == storageSlots[0][^1].Path.ToCommitment())
                {
                    break;
                }

                startRange = storageSlots[0][^1].Path.ToCommitment();
            }
            finally
            {
                storageSlots.DisposeRecursive();
                proofs?.Dispose();
            }
        }
    }

    [Test]
    public void TestWithHugeTree()
    {
        using var context = CreateContext();

        // generate Remote Tree
        using (var batch = context.BeginWriteBatch())
        {
            for (int accountIndex = 0; accountIndex < 10000; accountIndex++)
                batch.SetAccount(TestItem.GetRandomAddress(), TestItem.GenerateRandomAccount());
        }

        List<PathWithAccount> accountWithStorage = new();
        using (var batch = context.BeginWriteBatch())
        {
            for (int i = 1000; i < 10000; i += 1000)
            {
                Address address = TestItem.GetRandomAddress();
                Hash256 storagePath = address.ToAccountPath.ToCommitment();
                for (int j = 0; j < i; j += 1)
                    batch.SetSlot(storagePath, TestItem.GetRandomKeccak(), TestItem.GetRandomKeccak().Bytes.ToArray());
                batch.SetAccount(address, TestItem.GenerateRandomAccount());
                accountWithStorage.Add(new PathWithAccount(address.ToAccountPath, new Account(0)));
            }
        }

        // size of one PathWithAccount ranges from 39 -> 72
        (IOwnedReadOnlyList<PathWithAccount> accounts, IByteArrayList accountProofs)
            = context.Server.GetAccountRanges(context.RootHash, Keccak.Zero, Keccak.MaxValue, 10, CancellationToken.None);
        accounts.Count.Should().Be(1);
        accounts.Dispose();
        accountProofs.Dispose();

        (accounts, accountProofs) =
            context.Server.GetAccountRanges(context.RootHash, Keccak.Zero, Keccak.MaxValue, 100, CancellationToken.None);
        accounts.Count.Should().BeGreaterThan(2);
        accounts.Dispose();
        accountProofs.Dispose();

        (accounts, accountProofs) =
            context.Server.GetAccountRanges(context.RootHash, Keccak.Zero, Keccak.MaxValue, 10000, CancellationToken.None);
        accounts.Count.Should().BeGreaterThan(138);
        accounts.Dispose();
        accountProofs.Dispose();

        // TODO: Double check the threshold
        (accounts, accountProofs) =
            context.Server.GetAccountRanges(context.RootHash, Keccak.Zero, Keccak.MaxValue, 720000, CancellationToken.None);
        accounts.Count.Should().Be(10009);

        accounts.Dispose();
        accountProofs.Dispose();

        (accounts, accountProofs) =
            context.Server.GetAccountRanges(context.RootHash, Keccak.Zero, Keccak.MaxValue, 10000000, CancellationToken.None);
        accounts.Count.Should().Be(10009);
        accounts.Dispose();
        accountProofs.Dispose();

        var accountWithStorageArray = accountWithStorage.ToArray();

        (IOwnedReadOnlyList<IOwnedReadOnlyList<PathWithStorageSlot>> slots, IByteArrayList? proofs) = context.Server.GetStorageRanges(context.RootHash, accountWithStorageArray[..1], ValueKeccak.Zero, ValueKeccak.MaxValue, 10, CancellationToken.None);
        slots.Count.Should().Be(1);
        slots[0].Count.Should().Be(1);
        proofs.Should().NotBeNull();

        slots.DisposeRecursive();
        proofs?.Dispose();

        (slots, proofs) = context.Server.GetStorageRanges(context.RootHash, accountWithStorageArray[..1], ValueKeccak.Zero, ValueKeccak.MaxValue, 1000000, CancellationToken.None);
        slots.Count.Should().Be(1);
        slots[0].Count.Should().Be(1000);
        proofs?.Count.Should().Be(0);

        slots.DisposeRecursive();
        proofs?.Dispose();

        (slots, proofs) = context.Server.GetStorageRanges(context.RootHash, accountWithStorageArray[..2], ValueKeccak.Zero, ValueKeccak.MaxValue, 10, CancellationToken.None);
        slots.Count.Should().Be(1);
        slots[0].Count.Should().Be(1);
        proofs.Should().NotBeNull();
        slots.DisposeRecursive();
        proofs?.Dispose();

        (slots, proofs) = context.Server.GetStorageRanges(context.RootHash, accountWithStorageArray[..2], ValueKeccak.Zero, ValueKeccak.MaxValue, 100000, CancellationToken.None);
        slots.Count.Should().Be(2);
        slots[0].Count.Should().Be(1000);
        slots[1].Count.Should().Be(539);
        proofs.Should().NotBeNull();
        slots.DisposeRecursive();
        proofs?.Dispose();


        // incomplete tree will be returned as the hard limit is 2000000
        (slots, proofs) = context.Server.GetStorageRanges(context.RootHash, accountWithStorageArray, ValueKeccak.Zero, ValueKeccak.MaxValue, 3000000, CancellationToken.None);
        slots.Count.Should().Be(8);
        slots[^1].Count.Should().BeLessThan(8000);
        proofs?.Count.Should().BeGreaterThan(0);

        slots.DisposeRecursive();
        proofs?.Dispose();
    }
}
