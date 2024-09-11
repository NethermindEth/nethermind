// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.InteropServices.ComTypes;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.Verkle.Tree;
using Nethermind.Verkle.Tree.TreeStore;
using NUnit.Framework;

namespace Nethermind.Store.Test;

[TestFixture, Parallelizable(ParallelScope.All)]
public class VerkleTreeTests
{
    [Test]
    public void Create_commit_change_balance_get()
    {
        Account account = new(1);
        IDbProvider dbProvider = VerkleDbFactory.InitDatabase(DbMode.MemDb, null);
        var store = new VerkleTreeStore<VerkleSyncCache>(dbProvider, LimboLogs.Instance);
        VerkleStateTree stateTree = new VerkleStateTree(store, LimboLogs.Instance);
        stateTree.Set(TestItem.AddressA, account);
        stateTree.Commit();
        stateTree.CommitTree(0);

        account = account.WithChangedBalance(2);
        stateTree.Set(TestItem.AddressA, account);
        stateTree.Commit();
        stateTree.CommitTree(0);

        stateTree.TryGetStruct(TestItem.AddressA, out AccountStruct accountRestored);
        Assert.That(accountRestored.Balance, Is.EqualTo((UInt256)2));
    }

    [Test]
    public void Create_create_commit_change_balance_get()
    {
        Account account = new(1);
        IDbProvider dbProvider = VerkleDbFactory.InitDatabase(DbMode.MemDb, null);
        var store = new VerkleTreeStore<VerkleSyncCache>(dbProvider, LimboLogs.Instance);
        VerkleStateTree stateTree = new VerkleStateTree(store, LimboLogs.Instance);
        stateTree.Set(TestItem.AddressA, account);
        stateTree.Set(TestItem.AddressB, account);
        stateTree.Commit();
        stateTree.CommitTree(0);

        account = account.WithChangedBalance(2);
        stateTree.Set(TestItem.AddressA, account);
        stateTree.Commit();
        stateTree.CommitTree(0);

        stateTree.TryGetStruct(TestItem.AddressA, out AccountStruct accountRestored);
        Assert.That(accountRestored.Balance, Is.EqualTo((UInt256)2));
    }

    [Test]
    public void TestGenesis()
    {
        Account account = new(1);
        IDbProvider dbProvider = VerkleDbFactory.InitDatabase(DbMode.MemDb, null);
        var store = new VerkleTreeStore<VerkleSyncCache>(dbProvider, LimboLogs.Instance);
        VerkleStateTree stateTree = new VerkleStateTree(store, LimboLogs.Instance);
        stateTree.Set(new Address("0x0000000000000000000000000000000000000000"), account);
        stateTree.Commit();
        stateTree.CommitTree(0);
    }

    // [Test]
    // public void Create_commit_reset_change_balance_get()
    // {
    //     Account account = new(1);
    //     IDbProvider dbProvider = VerkleDbFactory.InitDatabase(DbMode.MemDb, null);
    //     VerkleStateTree stateTree = new VerkleStateTree(dbProvider);
    //     stateTree.Set(TestItem.AddressA, account);
    //     stateTree.Flush(0);
    //
    //     Keccak rootHash = new Keccak(stateTree.RootHash);
    //
    //     stateTree.Get(TestItem.AddressA);
    //     account = account.WithChangedBalance(2);
    //     stateTree.Set(TestItem.AddressA, account);
    //     stateTree.Flush(0);
    // }
    //
    // [TestCase(true, false)]
    // [TestCase(false, true)]
    // public void Commit_with_skip_root_should_skip_root(bool skipRoot, bool hasRoot)
    // {
    //     MemDb db = new();
    //     TrieStore trieStore = new TrieStore(db, LimboLogs.Instance);
    //     Account account = new(1);
    //
    //     IDbProvider dbProvider = VerkleDbFactory.InitDatabase(DbMode.MemDb, null);
    //     VerkleStateTree stateTree = new VerkleStateTree(dbProvider);
    //     stateTree.Set(TestItem.AddressA, account);
    //     stateTree.UpdateRootHash();
    //     Keccak stateRoot = stateTree.RootHash;
    //     stateTree.Flush(0, skipRoot);
    //
    //     if (hasRoot)
    //     {
    //         trieStore.LoadRlp(stateRoot).Length.Should().BeGreaterThan(0);
    //     }
    //     else
    //     {
    //         trieStore.Invoking(ts => ts.LoadRlp(stateRoot)).Should().Throw<TrieException>();
    //     }
    // }


    [Test]
    public void TestHiveState()
    {
        IDbProvider dbProvider = VerkleDbFactory.InitDatabase(DbMode.MemDb, null);
        var store = new VerkleTreeStore<VerkleSyncCache>(dbProvider, LimboLogs.Instance);
        VerkleWorldState worldState = new VerkleWorldState(store, dbProvider.CodeDb, LimboLogs.Instance);
        var address1 = new Address("0xfffffffffffffffffffffffffffffffffffffffe");
        worldState.CreateAccount(address1, 0, 1);
        worldState.InsertCode(address1, Bytes.FromHexString("0x60203611603157600143035f35116029575f356120000143116029576120005f3506545f5260205ff35b5f5f5260205ff35b5f5ffd00"), Prague.Instance);
        var address2 = new Address("0xa94f5374fce5edbc8e2a8697c15331677e6ebf0b");
        worldState.CreateAccount(address2, new UInt256(Bytes.FromHexString("0x3635c9adc5dea00000")), 0);
        worldState.Commit(Prague.Instance, true);
        worldState.CommitTree(0);
        Console.WriteLine(worldState.StateRoot);

        worldState.Set(new StorageCell(address1, 0), Bytes.FromHexString("0x00b2e892fbf04dcdbb33d71633d7cea0722aed27f8a9d0cf9912f97b34f9dadd"));
        worldState.SubtractFromBalance(address2, new UInt256(1000000000), Prague.Instance);
        worldState.IncrementNonce(address2);

        var emptyAccount = new Address("0x8a0a19589531694250d570040a0c4b74576919b8");
        var benef = new Address("0x2adc25665018aa1fe0e6bc666dac8fc2697ff9ba");
        worldState.AddToBalance(address2, new UInt256(999790000), Prague.Instance);
        worldState.CreateAccount(benef, new UInt256(63000), 0);
        worldState.CreateAccount(emptyAccount, 0, 0);

        worldState.Commit(Prague.Instance);
        worldState.CommitTree(1);
        Console.WriteLine(worldState.StateRoot);

        VerkleStateTree tree = new VerkleStateTree(store, LimboLogs.Instance);
        tree.Set(address1, new Account(1,0,Keccak.EmptyTreeHash, new Hash256("0xdf61faef43babbb1ebde8fd82ab9cb4cb74c240d0025138521477e073f72080a")));
        tree.Commit();
        Console.WriteLine(tree.StateRoot);
    }
}
