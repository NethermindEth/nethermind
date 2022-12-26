using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.State.Snap;
using Nethermind.Synchronization.SnapSync;
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

        IDbProvider dbProviderClient = new DbProvider(DbModeHint.Mem);
        var stateDbClient = new MemDb();
        dbProviderClient.RegisterDb(DbNames.State, stateDbClient);

        ProgressTracker progressTracker = new(null, dbProviderClient.StateDb, LimboLogs.Instance);
        SnapProvider snapProvider = new(progressTracker, dbProviderClient, LimboLogs.Instance);

        (PathWithAccount[] accounts, byte[][] proofs) =
            server.GetAccountRanges(tree.RootHash, Keccak.Zero, Keccak.MaxValue, 4000);

        AddRangeResult result = snapProvider.AddAccountRange(1, tree.RootHash, Keccak.Zero,
            accounts, proofs);

        Assert.AreEqual(AddRangeResult.OK, result);
        Assert.AreEqual(11, stateDbClient.Keys.Count);
        Assert.IsTrue(dbProviderClient.StateDb.KeyExists(tree.RootHash));
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

        IDbProvider dbProviderClient = new DbProvider(DbModeHint.Mem);
        MemDb stateDbClient = new();
        dbProviderClient.RegisterDb(DbNames.State, stateDbClient);

        ProgressTracker progressTracker = new(null, dbProviderClient.StateDb, LimboLogs.Instance);
        SnapProvider snapProvider = new(progressTracker, dbProviderClient, LimboLogs.Instance);
        Keccak startRange = Keccak.Zero;
        while (true)
        {
            (PathWithAccount[] accounts, byte[][] proofs) =
                server.GetAccountRanges(tree.RootHash, startRange, Keccak.MaxValue, 100);

            AddRangeResult result = snapProvider.AddAccountRange(1, tree.RootHash, startRange,
                accounts, proofs);

            Assert.AreEqual(AddRangeResult.OK, result);
            startRange = accounts[^1].Path;
            if (startRange.Bytes.SequenceEqual(TestItem.Tree.AccountsWithPaths[^1].Path.Bytes))
            {
                break;
            }
        }
        Assert.IsTrue(dbProviderClient.StateDb.KeyExists(tree.RootHash));
        Assert.AreEqual(11, stateDbClient.Keys.Count);
    }

    [Test]
    public void TestGetStorageRange()
    {

        MemDb stateDb = new MemDb();
        MemDb codeDb = new MemDb();
        TrieStore store = new(stateDb, LimboLogs.Instance);

        (StateTree InputStateTree, StorageTree InputStorageTree) = TestItem.Tree.GetTrees(store);

        SnapServer server = new(store.AsReadOnly(), codeDb, LimboLogs.Instance);

        IDbProvider dbProviderClient = new DbProvider(DbModeHint.Mem);
        dbProviderClient.RegisterDb(DbNames.State, new MemDb());
        dbProviderClient.RegisterDb(DbNames.Code, new MemDb());

        ProgressTracker progressTracker = new(null, dbProviderClient.StateDb, LimboLogs.Instance);
        SnapProvider snapProvider = new(progressTracker, dbProviderClient, LimboLogs.Instance);

        (List<PathWithStorageSlot[]> storageSlots, byte[][]? proofs) =
            server.GetStorageRanges(InputStateTree.RootHash, new PathWithAccount[] { TestItem.Tree.AccountsWithPaths[0] },
                Keccak.Zero, Keccak.MaxValue, 10);

        AddRangeResult result = snapProvider.AddStorageRange(1, null, InputStorageTree.RootHash, Keccak.Zero,
            storageSlots[0], proofs);

        Assert.AreEqual(AddRangeResult.OK, result);
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
            StorageTree storageTree = new(store, LimboLogs.Instance);
            Address address = TestItem.GetRandomAddress();
            for (int j = 0; j < i; j += 1)
            {
                storageTree.Set(TestItem.GetRandomKeccak(), TestItem.GetRandomKeccak().Bytes);
            }
            storageTree.Commit(1);
            var account = TestItem.GenerateRandomAccount().WithChangedStorageRoot(storageTree.RootHash);
            stateTree.Set(address, account);
            accountWithStorage.Add(new PathWithAccount() { Path = Keccak.Compute(address.Bytes), Account = account });
        }
        stateTree.Commit(1);
        Console.WriteLine(stateDb.Keys.Count);

        SnapServer server = new(store.AsReadOnly(), codeDb, LimboLogs.Instance);

        PathWithAccount[] accounts;
        // size of one PathWithAccount ranges from 39 -> 72
        (accounts, _) =
            server.GetAccountRanges(stateTree.RootHash, Keccak.Zero, Keccak.MaxValue, 10);
        accounts.Length.Should().Be(1);

        (accounts, _) =
            server.GetAccountRanges(stateTree.RootHash, Keccak.Zero, Keccak.MaxValue, 100);
        accounts.Length.Should().BeGreaterThan(2);

        (accounts, _) =
            server.GetAccountRanges(stateTree.RootHash, Keccak.Zero, Keccak.MaxValue, 10000);
        accounts.Length.Should().BeGreaterThan(138);

        // hard limit of nodes is 10000 - should not return greater than that
        (accounts, _) =
            server.GetAccountRanges(stateTree.RootHash, Keccak.Zero, Keccak.MaxValue, 720000);
        accounts.Length.Should().Be(10000);
        (accounts, _) =
            server.GetAccountRanges(stateTree.RootHash, Keccak.Zero, Keccak.MaxValue, 10000000);
        accounts.Length.Should().Be(10000);


        var accountWithStorageArray = accountWithStorage.ToArray();
        List<PathWithStorageSlot[]> slots;
        byte[][]? proofs;

        // the byteLimit is 10 but still returns the complete storage tree if the storageTree<hardByteLimit or numberOfNodes<10000
        (slots, proofs) =
            server.GetStorageRanges(stateTree.RootHash, accountWithStorageArray[..1], Keccak.Zero, Keccak.MaxValue, 10);
        slots.Count.Should().Be(1);
        slots[0].Length.Should().Be(1000);
        proofs.Should().BeNull();

        // only returns one storage tree due to low byte limit
        (slots, proofs) =
            server.GetStorageRanges(stateTree.RootHash, accountWithStorageArray[..2], Keccak.Zero, Keccak.MaxValue, 10);
        slots.Count.Should().Be(1);
        slots[0].Length.Should().Be(1000);
        proofs.Should().BeNull();

        // only returns one storage tree due to low byte limit
        (slots, proofs) =
            server.GetStorageRanges(stateTree.RootHash, accountWithStorageArray[..2], Keccak.Zero, Keccak.MaxValue, 100000);
        slots.Count.Should().Be(2);
        slots[0].Length.Should().Be(1000);
        slots[1].Length.Should().Be(2000);
        proofs.Should().BeNull();


        // incomplete tree will be returned as the hard limit is 2000000
        (slots, proofs) =
            server.GetStorageRanges(stateTree.RootHash, accountWithStorageArray, Keccak.Zero, Keccak.MaxValue, 3000000);
        slots.Count.Should().Be(8);
        slots[^1].Length.Should().BeLessThan(8000);
        proofs.Should().NotBeEmpty();
    }
}
