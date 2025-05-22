using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Libp2p.Core.Enums;
using Nethermind.Logging;
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

public class SnapServerTest
{
    private class Context
    {
        internal SnapServer Server { get; init; } = null!;
        internal SnapProvider SnapProvider { get; init; } = null!;
        internal StateTree Tree { get; init; } = null!;
        internal MemDb ClientStateDb { get; init; } = null!;
    }

    private Context CreateContext(IStateReader? stateRootTracker = null, ILastNStateRootTracker? lastNStateRootTracker = null)
    {
        MemDb stateDbServer = new();
        MemDb codeDbServer = new();
        TrieStore store = TestTrieStoreFactory.Build(stateDbServer, LimboLogs.Instance);
        StateTree tree = new(store, LimboLogs.Instance);
        SnapServer server = new(store.AsReadOnly(), codeDbServer, stateRootTracker ?? CreateConstantStateRootTracker(true), LimboLogs.Instance, lastNStateRootTracker);

        MemDb clientStateDb = new();
        using ProgressTracker progressTracker = new(clientStateDb, new TestSyncConfig(), new StateSyncPivot(null!, new TestSyncConfig(), LimboLogs.Instance), LimboLogs.Instance);

        INodeStorage nodeStorage = new NodeStorage(clientStateDb);

        SnapProvider snapProvider = new(progressTracker, new MemDb(), nodeStorage, LimboLogs.Instance);

        return new Context
        {
            Server = server,
            SnapProvider = snapProvider,
            Tree = tree,
            ClientStateDb = clientStateDb
        };
    }

    [Test]
    public void TestGetAccountRange()
    {
        Context context = CreateContext();
        TestItem.Tree.FillStateTreeWithTestAccounts(context.Tree);

        (IOwnedReadOnlyList<PathWithAccount> accounts, IOwnedReadOnlyList<byte[]> proofs) =
            context.Server.GetAccountRanges(context.Tree.RootHash, Keccak.Zero, Keccak.MaxValue, 4000, CancellationToken.None);

        AddRangeResult result = context.SnapProvider.AddAccountRange(1, context.Tree.RootHash, Keccak.Zero,
            accounts.ToArray(), proofs.ToArray());

        result.Should().Be(AddRangeResult.OK);
        context.ClientStateDb.Keys.Count.Should().Be(10);
        accounts.Dispose();
        proofs.Dispose();
    }

    [Test]
    public void TestGetAccountRange_InvalidRange()
    {
        Context context = CreateContext();
        TestItem.Tree.FillStateTreeWithTestAccounts(context.Tree);

        (IOwnedReadOnlyList<PathWithAccount> accounts, IOwnedReadOnlyList<byte[]> proofs) =
            context.Server.GetAccountRanges(context.Tree.RootHash, Keccak.MaxValue, Keccak.Zero, 4000, CancellationToken.None);

        accounts.Count.Should().Be(0);
        accounts.Dispose();
        proofs.Dispose();
    }

    [Test]
    public void TestGetTrieNode_Root()
    {
        Context context = CreateContext();
        TestItem.Tree.FillStateTreeWithTestAccounts(context.Tree);

        using IOwnedReadOnlyList<byte[]> result = context.Server.GetTrieNodes([
            new PathGroup()
            {
                Group = [[]]
            }
        ], context.Tree.RootHash, default)!;

        result.Count.Should().Be(1);
    }

    [Test]
    public void TestGetTrieNode_Storage_Root()
    {
        Context context = CreateContext();
        TestItem.Tree.FillStateTreeWithTestAccounts(context.Tree);

        using IOwnedReadOnlyList<byte[]> result = context.Server.GetTrieNodes([
            new PathGroup()
            {
                Group = [TestItem.Tree.AccountsWithPaths[0].Path.Bytes.ToArray(), []]
            }
        ], context.Tree.RootHash, default)!;

        result.Count.Should().Be(1);
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

        Context context = CreateContext(stateRootTracker: CreateConstantStateRootTracker(withLastNStateTracker), lastNStateRootTracker: lastNStateTracker);

        (IOwnedReadOnlyList<PathWithAccount> accounts, IOwnedReadOnlyList<byte[]> accountProofs) =
            context.Server.GetAccountRanges(context.Tree.RootHash, Keccak.Zero, Keccak.MaxValue, 4000, CancellationToken.None);

        accounts.Count.Should().Be(0);

        (IOwnedReadOnlyList<IOwnedReadOnlyList<PathWithStorageSlot>> storageSlots, IOwnedReadOnlyList<byte[]>? proofs) =
            context.Server.GetStorageRanges(context.Tree.RootHash, [TestItem.Tree.AccountsWithPaths[0]],
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
        Context context = CreateContext();
        TestItem.Tree.FillStateTreeWithTestAccounts(context.Tree);

        Hash256 startRange = Keccak.Zero;
        while (true)
        {
            (IOwnedReadOnlyList<PathWithAccount> accounts, IOwnedReadOnlyList<byte[]> proofs) =
                context.Server.GetAccountRanges(context.Tree.RootHash, startRange, Keccak.MaxValue, 100, CancellationToken.None);

            try
            {
                AddRangeResult result = context.SnapProvider.AddAccountRange(1, context.Tree.RootHash, startRange,
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
        context.ClientStateDb.Keys.Count.Should().Be(10);
    }

    [TestCase(10, 10)]
    [TestCase(10000, 1000)]
    [TestCase(10000, 10000000)]
    [TestCase(10000, 10000)]
    public void TestGetAccountRangeMultipleLarger(int stateSize, int byteLimit)
    {
        Context context = CreateContext();
        TestItem.Tree.FillStateTreeMultipleAccount(context.Tree, stateSize);

        Hash256 startRange = Keccak.Zero;
        while (true)
        {
            (IOwnedReadOnlyList<PathWithAccount> accounts, IOwnedReadOnlyList<byte[]> proofs) =
                context.Server.GetAccountRanges(context.Tree.RootHash, startRange, Keccak.MaxValue, byteLimit, CancellationToken.None);

            try
            {
                AddRangeResult result = context.SnapProvider.AddAccountRange(1, context.Tree.RootHash, startRange,
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
        Context context = CreateContext();
        TestItem.Tree.FillStateTreeMultipleAccount(context.Tree, stateSize);
        Hash256 startRange = Keccak.Zero;

        ValueHash256 limit = new ValueHash256("0x8000000000000000000000000000000000000000000000000000000000000000");
        while (true)
        {
            (IOwnedReadOnlyList<PathWithAccount> accounts, IOwnedReadOnlyList<byte[]> proofs) = context.Server
                .GetAccountRanges(context.Tree.RootHash, startRange, limit, byteLimit, CancellationToken.None);

            try
            {
                AddRangeResult result = context.SnapProvider.AddAccountRange(1, context.Tree.RootHash, startRange,
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
        MemDb stateDb = new MemDb();
        MemDb codeDb = new MemDb();
        TrieStore store = TestTrieStoreFactory.Build(stateDb, LimboLogs.Instance);

        (StateTree inputStateTree, StorageTree inputStorageTree, Hash256 _) = TestItem.Tree.GetTrees(store);

        SnapServer server = new(store.AsReadOnly(), codeDb, CreateConstantStateRootTracker(true), LimboLogs.Instance);

        IDbProvider dbProviderClient = new DbProvider();
        dbProviderClient.RegisterDb(DbNames.State, new MemDb());
        dbProviderClient.RegisterDb(DbNames.Code, new MemDb());

        using ProgressTracker progressTracker = new(dbProviderClient.StateDb, new TestSyncConfig(), new StateSyncPivot(null!, new TestSyncConfig(), LimboLogs.Instance), LimboLogs.Instance);
        SnapProvider snapProvider = new(progressTracker, dbProviderClient.CodeDb, new NodeStorage(dbProviderClient.StateDb), LimboLogs.Instance);

        (IOwnedReadOnlyList<IOwnedReadOnlyList<PathWithStorageSlot>> storageSlots, IOwnedReadOnlyList<byte[]>? proofs) =
            server.GetStorageRanges(inputStateTree.RootHash, [TestItem.Tree.AccountsWithPaths[0]],
                ValueKeccak.Zero, ValueKeccak.MaxValue, 10, CancellationToken.None);

        try
        {
            var storageRangeRequest = new StorageRange()
            {
                StartingHash = Keccak.Zero,
                Accounts = new ArrayPoolList<PathWithAccount>(1) { new(TestItem.Tree.AccountsWithPaths[0].Path, new Account(UInt256.Zero).WithChangedStorageRoot(inputStorageTree.RootHash)) }
            };
            AddRangeResult result = snapProvider.AddStorageRangeForAccount(storageRangeRequest, 0, storageSlots[0], proofs);

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
        MemDb stateDb = new MemDb();
        MemDb codeDb = new MemDb();
        TrieStore store = TestTrieStoreFactory.Build(stateDb, LimboLogs.Instance);

        (StateTree inputStateTree, StorageTree inputStorageTree, Hash256 _) = TestItem.Tree.GetTrees(store);

        SnapServer server = new(store.AsReadOnly(), codeDb, CreateConstantStateRootTracker(true), LimboLogs.Instance);

        IDbProvider dbProviderClient = new DbProvider();
        dbProviderClient.RegisterDb(DbNames.State, new MemDb());
        dbProviderClient.RegisterDb(DbNames.Code, new MemDb());

        ValueHash256 lastStorageHash = TestItem.Tree.SlotsWithPaths[^1].Path;
        var asInt = lastStorageHash.ToUInt256();
        ValueHash256 beyondLast = new ValueHash256((++asInt).ToBigEndian());

        (IOwnedReadOnlyList<IOwnedReadOnlyList<PathWithStorageSlot>> storageSlots, IOwnedReadOnlyList<byte[]>? proofs) =
            server.GetStorageRanges(inputStateTree.RootHash, [TestItem.Tree.AccountsWithPaths[0]],
                beyondLast, ValueKeccak.MaxValue, 10, CancellationToken.None);

        storageSlots.Count.Should().Be(0);
        proofs?.Count.Should().BeGreaterThan(0); //in worst case should get at least root node

        storageSlots.DisposeRecursive();
        proofs?.Dispose();
    }

    [Test]
    public void TestGetStorageRangeMulti()
    {
        MemDb stateDb = new MemDb();
        MemDb codeDb = new MemDb();
        TrieStore store = TestTrieStoreFactory.Build(stateDb, LimboLogs.Instance);

        (StateTree inputStateTree, StorageTree inputStorageTree, Hash256 _) = TestItem.Tree.GetTrees(store, 10000);

        SnapServer server = new(store.AsReadOnly(), codeDb, CreateConstantStateRootTracker(true), LimboLogs.Instance);

        IDbProvider dbProviderClient = new DbProvider();
        dbProviderClient.RegisterDb(DbNames.State, new MemDb());
        dbProviderClient.RegisterDb(DbNames.Code, new MemDb());

        using ProgressTracker progressTracker = new(dbProviderClient.StateDb, new TestSyncConfig(), new StateSyncPivot(null!, new TestSyncConfig(), LimboLogs.Instance), LimboLogs.Instance);
        SnapProvider snapProvider = new(progressTracker, dbProviderClient.CodeDb, new NodeStorage(dbProviderClient.StateDb), LimboLogs.Instance);

        Hash256 startRange = Keccak.Zero;
        while (true)
        {
            (IOwnedReadOnlyList<IOwnedReadOnlyList<PathWithStorageSlot>> storageSlots, IOwnedReadOnlyList<byte[]>? proofs) =
                server.GetStorageRanges(inputStateTree.RootHash, [TestItem.Tree.AccountsWithPaths[0]],
                    startRange, ValueKeccak.MaxValue, 10000, CancellationToken.None);

            try
            {
                var storageRangeRequest = new StorageRange()
                {
                    StartingHash = startRange,
                    Accounts = new ArrayPoolList<PathWithAccount>(1) { new(TestItem.Tree.AccountsWithPaths[0].Path, new Account(UInt256.Zero).WithChangedStorageRoot(inputStorageTree.RootHash)) }
                };
                AddRangeResult result = snapProvider.AddStorageRangeForAccount(storageRangeRequest, 0, storageSlots[0], proofs);

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
        MemDb stateDb = new MemDb();
        MemDb codeDb = new MemDb();
        TrieStore store = TestTrieStoreFactory.Build(stateDb, LimboLogs.Instance);

        StateTree stateTree = new(store, LimboLogs.Instance);

        // generate Remote Tree
        for (int accountIndex = 0; accountIndex < 10000; accountIndex++)
        {
            stateTree.Set(TestItem.GetRandomAddress(), TestItem.GenerateRandomAccount());
        }
        stateTree.Commit();

        List<PathWithAccount> accountWithStorage = new();
        for (int i = 1000; i < 10000; i += 1000)
        {
            Address address = TestItem.GetRandomAddress();
            StorageTree storageTree = new(store.GetTrieStore(address), LimboLogs.Instance);
            for (int j = 0; j < i; j += 1)
            {
                storageTree.Set(TestItem.GetRandomKeccak(), TestItem.GetRandomKeccak().Bytes.ToArray());
            }
            storageTree.Commit();
            var account = TestItem.GenerateRandomAccount().WithChangedStorageRoot(storageTree.RootHash);
            stateTree.Set(address, account);
            accountWithStorage.Add(new PathWithAccount() { Path = Keccak.Compute(address.Bytes), Account = account });
        }
        stateTree.Commit();

        SnapServer server = new(store.AsReadOnly(), codeDb, CreateConstantStateRootTracker(true), LimboLogs.Instance);

        // size of one PathWithAccount ranges from 39 -> 72
        (IOwnedReadOnlyList<PathWithAccount> accounts, IOwnedReadOnlyList<byte[]> accountProofs)
            = server.GetAccountRanges(stateTree.RootHash, Keccak.Zero, Keccak.MaxValue, 10, CancellationToken.None);
        accounts.Count.Should().Be(1);
        accounts.Dispose();
        accountProofs.Dispose();

        (accounts, accountProofs) =
            server.GetAccountRanges(stateTree.RootHash, Keccak.Zero, Keccak.MaxValue, 100, CancellationToken.None);
        accounts.Count.Should().BeGreaterThan(2);
        accounts.Dispose();
        accountProofs.Dispose();

        (accounts, accountProofs) =
            server.GetAccountRanges(stateTree.RootHash, Keccak.Zero, Keccak.MaxValue, 10000, CancellationToken.None);
        accounts.Count.Should().BeGreaterThan(138);
        accounts.Dispose();
        accountProofs.Dispose();

        // TODO: Double check the threshold
        (accounts, accountProofs) =
            server.GetAccountRanges(stateTree.RootHash, Keccak.Zero, Keccak.MaxValue, 720000, CancellationToken.None);
        accounts.Count.Should().Be(10009);

        accounts.Dispose();
        accountProofs.Dispose();

        (accounts, accountProofs) =
            server.GetAccountRanges(stateTree.RootHash, Keccak.Zero, Keccak.MaxValue, 10000000, CancellationToken.None);
        accounts.Count.Should().Be(10009);
        accounts.Dispose();
        accountProofs.Dispose();

        var accountWithStorageArray = accountWithStorage.ToArray();

        (IOwnedReadOnlyList<IOwnedReadOnlyList<PathWithStorageSlot>> slots, IOwnedReadOnlyList<byte[]>? proofs) = server.GetStorageRanges(stateTree.RootHash, accountWithStorageArray[..1], ValueKeccak.Zero, ValueKeccak.MaxValue, 10, CancellationToken.None);
        slots.Count.Should().Be(1);
        slots[0].Count.Should().Be(1);
        proofs.Should().NotBeNull();

        slots.DisposeRecursive();
        proofs?.Dispose();

        (slots, proofs) = server.GetStorageRanges(stateTree.RootHash, accountWithStorageArray[..1], ValueKeccak.Zero, ValueKeccak.MaxValue, 1000000, CancellationToken.None);
        slots.Count.Should().Be(1);
        slots[0].Count.Should().Be(1000);
        proofs.Should().BeEmpty();

        slots.DisposeRecursive();
        proofs?.Dispose();

        (slots, proofs) = server.GetStorageRanges(stateTree.RootHash, accountWithStorageArray[..2], ValueKeccak.Zero, ValueKeccak.MaxValue, 10, CancellationToken.None);
        slots.Count.Should().Be(1);
        slots[0].Count.Should().Be(1);
        proofs.Should().NotBeNull();
        slots.DisposeRecursive();
        proofs?.Dispose();

        (slots, proofs) = server.GetStorageRanges(stateTree.RootHash, accountWithStorageArray[..2], ValueKeccak.Zero, ValueKeccak.MaxValue, 100000, CancellationToken.None);
        slots.Count.Should().Be(2);
        slots[0].Count.Should().Be(1000);
        slots[1].Count.Should().Be(539);
        proofs.Should().NotBeNull();
        slots.DisposeRecursive();
        proofs?.Dispose();


        // incomplete tree will be returned as the hard limit is 2000000
        (slots, proofs) = server.GetStorageRanges(stateTree.RootHash, accountWithStorageArray, ValueKeccak.Zero, ValueKeccak.MaxValue, 3000000, CancellationToken.None);
        slots.Count.Should().Be(8);
        slots[^1].Count.Should().BeLessThan(8000);
        proofs.Should().NotBeEmpty();

        slots.DisposeRecursive();
        proofs?.Dispose();
    }

    private IStateReader CreateConstantStateRootTracker(bool available)
    {
        IStateReader tracker = Substitute.For<IStateReader>();
        tracker.HasStateForRoot(Arg.Any<Hash256>()).Returns(available);
        return tracker;
    }
}
