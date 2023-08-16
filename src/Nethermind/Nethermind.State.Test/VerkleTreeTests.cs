// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;
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
        VerkleStateTree stateTree = new VerkleStateTree(dbProvider, LimboLogs.Instance);
        stateTree.Set(TestItem.AddressA, account);
        stateTree.Commit();
        stateTree.CommitTree(0);

        account = account.WithChangedBalance(2);
        stateTree.Set(TestItem.AddressA, account);
        stateTree.Commit();
        stateTree.CommitTree(0);

        Account accountRestored = stateTree.Get(TestItem.AddressA);
        Assert.That(accountRestored.Balance, Is.EqualTo((UInt256)2));
    }

    [Test]
    public void Create_create_commit_change_balance_get()
    {
        Account account = new(1);
        IDbProvider dbProvider = VerkleDbFactory.InitDatabase(DbMode.MemDb, null);
        VerkleStateTree stateTree = new VerkleStateTree(dbProvider, LimboLogs.Instance);
        stateTree.Set(TestItem.AddressA, account);
        stateTree.Set(TestItem.AddressB, account);
        stateTree.Commit();
        stateTree.CommitTree(0);

        account = account.WithChangedBalance(2);
        stateTree.Set(TestItem.AddressA, account);
        stateTree.Commit();
        stateTree.CommitTree(0);

        Account accountRestored = stateTree.Get(TestItem.AddressA);
        Assert.That(accountRestored.Balance, Is.EqualTo((UInt256)2));
    }

    [Test]
    public void TestGenesis()
    {
        Account account = new(1);
        IDbProvider dbProvider = VerkleDbFactory.InitDatabase(DbMode.MemDb, null);
        VerkleStateTree stateTree = new VerkleStateTree(dbProvider, LimboLogs.Instance);
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
}
