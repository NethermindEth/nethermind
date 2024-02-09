using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.State.Snap;
using Nethermind.Synchronization.SnapSync;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.SnapSync;

public class SnapServerTest
{
    [Test]
    public void TestGetAccountRange()
    {
        MemDb stateDbServer = new();
        MemDb codeDbServer = new();
        TrieStore store = new(stateDbServer, LimboLogs.Instance);
        StateTree tree = new(store, LimboLogs.Instance);
        TestItem.Tree.FillStateTreeWithTestAccounts(tree);

        SnapServer server = new(store.AsReadOnly(), codeDbServer, LimboLogs.Instance);

        IDbProvider dbProviderClient = new DbProvider();
        var stateDbClient = new MemDb();
        var nodeStorage = new NodeStorage(stateDbClient);
        dbProviderClient.RegisterDb(DbNames.State, stateDbClient);
        ProgressTracker progressTracker = new(null!, dbProviderClient.StateDb, LimboLogs.Instance);

        SnapProvider snapProvider = new(progressTracker, new MemDb(), nodeStorage, LimboLogs.Instance);

        (PathWithAccount[] accounts, byte[][] proofs) =
            server.GetAccountRanges(tree.RootHash, Keccak.Zero, Keccak.MaxValue, 4000, CancellationToken.None);

        AddRangeResult result = snapProvider.AddAccountRange(1, tree.RootHash, Keccak.Zero,
            accounts, proofs);

        result.Should().Be(AddRangeResult.OK);
        stateDbClient.Keys.Count.Should().Be(10);
    }

    [Test]
    public void TestGetAccountRangeMultiple()
    {
        MemDb stateDbServer = new();
        MemDb codeDbServer = new();
        TrieStore store = new(stateDbServer, LimboLogs.Instance);

        StateTree tree = new(store, LimboLogs.Instance);

        TestItem.Tree.FillStateTreeWithTestAccounts(tree);

        SnapServer server = new(store.AsReadOnly(), codeDbServer, LimboLogs.Instance);

        IDbProvider dbProviderClient = new DbProvider();
        MemDb stateDbClient = new();
        dbProviderClient.RegisterDb(DbNames.State, stateDbClient);

        ProgressTracker progressTracker = new(null!, dbProviderClient.StateDb, LimboLogs.Instance);
        SnapProvider snapProvider = new(progressTracker, new MemDb(), new NodeStorage(stateDbClient), LimboLogs.Instance);
        Hash256 startRange = Keccak.Zero;
        while (true)
        {
            (PathWithAccount[] accounts, byte[][] proofs) =
                server.GetAccountRanges(tree.RootHash, startRange, Keccak.MaxValue, 100, CancellationToken.None);

            AddRangeResult result = snapProvider.AddAccountRange(1, tree.RootHash, startRange,
                accounts, proofs);

            result.Should().Be(AddRangeResult.OK);
            startRange = accounts[^1].Path.ToCommitment();
            if (startRange.Bytes.SequenceEqual(TestItem.Tree.AccountsWithPaths[^1].Path.Bytes))
            {
                break;
            }
        }
        stateDbClient.Keys.Count.Should().Be(10);
    }

    [TestCase(10, 10)]
    [TestCase(10000, 10)]
    [TestCase(10000, 10000000)]
    [TestCase(10000, 10000)]
    public void TestGetAccountRangeMultipleLarger(int stateSize, int byteLimit)
    {
        MemDb stateDbServer = new();
        MemDb codeDbServer = new();
        TrieStore store = new(stateDbServer, LimboLogs.Instance);

        StateTree tree = new(store, LimboLogs.Instance);

        TestItem.Tree.FillStateTreeMultipleAccount(tree, stateSize);

        SnapServer server = new(store.AsReadOnly(), codeDbServer, LimboLogs.Instance);

        IDbProvider dbProviderClient = new DbProvider();
        MemDb stateDbClient = new();
        dbProviderClient.RegisterDb(DbNames.State, stateDbClient);

        ProgressTracker progressTracker = new(null!, dbProviderClient.StateDb, LimboLogs.Instance);
        SnapProvider snapProvider = new(progressTracker, new MemDb(), new NodeStorage(stateDbClient), LimboLogs.Instance);
        Hash256 startRange = Keccak.Zero;
        while (true)
        {
            (PathWithAccount[] accounts, byte[][] proofs) =
                server.GetAccountRanges(tree.RootHash, startRange, Keccak.MaxValue, byteLimit, CancellationToken.None);

            AddRangeResult result = snapProvider.AddAccountRange(1, tree.RootHash, startRange,
                accounts, proofs);

            result.Should().Be(AddRangeResult.OK);
            if (startRange == accounts[^1].Path.ToCommitment())
            {
                break;
            }
            startRange = accounts[^1].Path.ToCommitment();
        }
    }

    [TestCase(10, 10)]
    [TestCase(10000, 10)]
    [TestCase(100, 100)]
    [TestCase(10000, 10000000)]
    public void TestGetAccountRangeArtificialLimit(int stateSize, int byteLimit)
    {
        MemDb stateDbServer = new();
        MemDb codeDbServer = new();
        TrieStore store = new(stateDbServer, LimboLogs.Instance);

        StateTree tree = new(store, LimboLogs.Instance);

        TestItem.Tree.FillStateTreeMultipleAccount(tree, stateSize);

        SnapServer server = new(store.AsReadOnly(), codeDbServer, LimboLogs.Instance);

        IDbProvider dbProviderClient = new DbProvider();
        MemDb stateDbClient = new();
        dbProviderClient.RegisterDb(DbNames.State, stateDbClient);

        ProgressTracker progressTracker = new(null!, dbProviderClient.StateDb, LimboLogs.Instance);
        SnapProvider snapProvider = new(progressTracker, new MemDb(), new NodeStorage(stateDbClient), LimboLogs.Instance);
        Hash256 startRange = Keccak.Zero;

        ValueHash256 limit = new ValueHash256("0x8000000000000000000000000000000000000000000000000000000000000000");
        while (true)
        {
            (PathWithAccount[] accounts, byte[][] proofs) = server
                .GetAccountRanges(tree.RootHash, startRange, limit, byteLimit, CancellationToken.None);

            AddRangeResult result = snapProvider.AddAccountRange(1, tree.RootHash, startRange,
                accounts, proofs);

            result.Should().Be(AddRangeResult.OK);
            if (startRange == accounts[^1].Path.ToCommitment())
            {
                break;
            }
            startRange = accounts[^1].Path.ToCommitment();
        }
    }

    [Test]
    public void TestGetStorageRange()
    {
        MemDb stateDb = new MemDb();
        MemDb codeDb = new MemDb();
        TrieStore store = new(stateDb, LimboLogs.Instance);

        (StateTree InputStateTree, StorageTree InputStorageTree, Hash256 account) = TestItem.Tree.GetTrees(store);

        SnapServer server = new(store.AsReadOnly(), codeDb, LimboLogs.Instance);

        IDbProvider dbProviderClient = new DbProvider();
        dbProviderClient.RegisterDb(DbNames.State, new MemDb());
        dbProviderClient.RegisterDb(DbNames.Code, new MemDb());

        ProgressTracker progressTracker = new(null!, dbProviderClient.StateDb, LimboLogs.Instance);
        SnapProvider snapProvider = new(progressTracker, dbProviderClient.CodeDb, new NodeStorage(dbProviderClient.StateDb), LimboLogs.Instance);

        (PathWithStorageSlot[][] storageSlots, byte[][]? proofs) =
            server.GetStorageRanges(InputStateTree.RootHash, new PathWithAccount[] { TestItem.Tree.AccountsWithPaths[0] },
                Keccak.Zero, Keccak.MaxValue, 10, CancellationToken.None);

        AddRangeResult result = snapProvider.AddStorageRange(1, TestItem.Tree.AccountsWithPaths[0], InputStorageTree.RootHash, Keccak.Zero,
            storageSlots[0], proofs);

        result.Should().Be(AddRangeResult.OK);
    }

    [Test]
    public void TestGetStorageRangeMulti()
    {
        MemDb stateDb = new MemDb();
        MemDb codeDb = new MemDb();
        TrieStore store = new(stateDb, LimboLogs.Instance);

        (StateTree InputStateTree, StorageTree InputStorageTree, Hash256 account) = TestItem.Tree.GetTrees(store, 10000);

        SnapServer server = new(store.AsReadOnly(), codeDb, LimboLogs.Instance);

        IDbProvider dbProviderClient = new DbProvider();
        dbProviderClient.RegisterDb(DbNames.State, new MemDb());
        dbProviderClient.RegisterDb(DbNames.Code, new MemDb());

        ProgressTracker progressTracker = new(null!, dbProviderClient.StateDb, LimboLogs.Instance);
        SnapProvider snapProvider = new(progressTracker, dbProviderClient.CodeDb, new NodeStorage(dbProviderClient.StateDb), LimboLogs.Instance);

        Hash256 startRange = Keccak.Zero;
        while (true)
        {
            (PathWithStorageSlot[][] storageSlots, byte[][]? proofs) =
                server.GetStorageRanges(InputStateTree.RootHash, new PathWithAccount[] { TestItem.Tree.AccountsWithPaths[0] },
                    startRange, Keccak.MaxValue, 10000, CancellationToken.None);

            AddRangeResult result = snapProvider.AddStorageRange(1, TestItem.Tree.AccountsWithPaths[0], InputStorageTree.RootHash, startRange,
                storageSlots[0], proofs);

            result.Should().Be(AddRangeResult.OK);
            if (startRange == storageSlots[0][^1].Path.ToCommitment())
            {
                break;
            }
            startRange = storageSlots[0][^1].Path.ToCommitment();
        }
    }

    [Test]
    public void TestWithHugeTree()
    {
        MemDb stateDb = new MemDb();
        MemDb codeDb = new MemDb();
        TrieStore store = new(stateDb, LimboLogs.Instance);

        StateTree stateTree = new(store, LimboLogs.Instance);

        // generate Remote Tree
        for (int accountIndex = 0; accountIndex < 10000; accountIndex++)
        {
            stateTree.Set(TestItem.GetRandomAddress(), TestItem.GenerateRandomAccount());
        }
        stateTree.Commit(0);

        List<PathWithAccount> accountWithStorage = new();
        for (int i = 1000; i < 10000; i += 1000)
        {
            Address address = TestItem.GetRandomAddress();
            StorageTree storageTree = new(store.GetTrieStore(address.ToAccountPath), LimboLogs.Instance);
            for (int j = 0; j < i; j += 1)
            {
                storageTree.Set(TestItem.GetRandomKeccak(), TestItem.GetRandomKeccak().Bytes.ToArray());
            }
            storageTree.Commit(1);
            var account = TestItem.GenerateRandomAccount().WithChangedStorageRoot(storageTree.RootHash);
            stateTree.Set(address, account);
            accountWithStorage.Add(new PathWithAccount() { Path = Keccak.Compute(address.Bytes), Account = account });
        }
        stateTree.Commit(1);

        SnapServer server = new(store.AsReadOnly(), codeDb, LimboLogs.Instance);

        PathWithAccount[] accounts;
        // size of one PathWithAccount ranges from 39 -> 72
        (accounts, _) =
            server.GetAccountRanges(stateTree.RootHash, Keccak.Zero, Keccak.MaxValue, 10, CancellationToken.None);
        accounts.Length.Should().Be(1);

        (accounts, _) =
            server.GetAccountRanges(stateTree.RootHash, Keccak.Zero, Keccak.MaxValue, 100, CancellationToken.None);
        accounts.Length.Should().BeGreaterThan(2);

        (accounts, _) =
            server.GetAccountRanges(stateTree.RootHash, Keccak.Zero, Keccak.MaxValue, 10000, CancellationToken.None);
        accounts.Length.Should().BeGreaterThan(138);

        // TODO: Double check the threshold
        (accounts, _) =
            server.GetAccountRanges(stateTree.RootHash, Keccak.Zero, Keccak.MaxValue, 720000, CancellationToken.None);
        accounts.Length.Should().Be(10009);
        (accounts, _) =
            server.GetAccountRanges(stateTree.RootHash, Keccak.Zero, Keccak.MaxValue, 10000000, CancellationToken.None);
        accounts.Length.Should().Be(10009);


        var accountWithStorageArray = accountWithStorage.ToArray();
        PathWithStorageSlot[][] slots;
        byte[][]? proofs;

        (slots, proofs) =
            server.GetStorageRanges(stateTree.RootHash, accountWithStorageArray[..1], Keccak.Zero, Keccak.MaxValue, 10, CancellationToken.None);
        slots.Length.Should().Be(1);
        slots[0].Length.Should().Be(1);
        proofs.Should().NotBeNull();

        (slots, proofs) =
            server.GetStorageRanges(stateTree.RootHash, accountWithStorageArray[..1], Keccak.Zero, Keccak.MaxValue, 1000000, CancellationToken.None);
        slots.Length.Should().Be(1);
        slots[0].Length.Should().Be(1000);
        proofs.Should().BeEmpty();

        (slots, proofs) =
            server.GetStorageRanges(stateTree.RootHash, accountWithStorageArray[..2], Keccak.Zero, Keccak.MaxValue, 10, CancellationToken.None);
        slots.Length.Should().Be(1);
        slots[0].Length.Should().Be(1);
        proofs.Should().NotBeNull();

        (slots, proofs) =
            server.GetStorageRanges(stateTree.RootHash, accountWithStorageArray[..2], Keccak.Zero, Keccak.MaxValue, 100000, CancellationToken.None);
        slots.Length.Should().Be(2);
        slots[0].Length.Should().Be(1000);
        slots[1].Length.Should().Be(539);
        proofs.Should().NotBeNull();


        // incomplete tree will be returned as the hard limit is 2000000
        (slots, proofs) =
            server.GetStorageRanges(stateTree.RootHash, accountWithStorageArray, Keccak.Zero, Keccak.MaxValue, 3000000, CancellationToken.None);
        slots.Length.Should().Be(8);
        slots[^1].Length.Should().BeLessThan(8000);
        proofs.Should().NotBeEmpty();
    }
}
