// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Store.Test
{
    [TestFixture, Parallelizable(ParallelScope.All)]
    public class PatriciaTreeTests
    {
        [Test]
        public void Create_commit_change_balance_get()
        {
            Account account = new(1);
            StateTree stateTree = new();
            stateTree.Set(TestItem.AddressA, account);
            stateTree.Commit(0);

            account = account.WithChangedBalance(2);
            stateTree.Set(TestItem.AddressA, account);
            stateTree.Commit(0);

            Account accountRestored = stateTree.Get(TestItem.AddressA);
            Assert.That(accountRestored.Balance, Is.EqualTo((UInt256)2));
        }

        [Test]
        public void Create_create_commit_change_balance_get()
        {
            Account account = new(1);
            StateTree stateTree = new();
            stateTree.Set(TestItem.AddressA, account);
            stateTree.Set(TestItem.AddressB, account);
            stateTree.Commit(0);

            account = account.WithChangedBalance(2);
            stateTree.Set(TestItem.AddressA, account);
            stateTree.Commit(0);

            Account accountRestored = stateTree.Get(TestItem.AddressA);
            Assert.That(accountRestored.Balance, Is.EqualTo((UInt256)2));
        }

        [Test]
        public void Create_commit_reset_change_balance_get()
        {
            MemDb db = new();
            Account account = new(1);
            StateTree stateTree = new(new TrieStore(db, LimboLogs.Instance), LimboLogs.Instance);
            stateTree.Set(TestItem.AddressA, account);
            stateTree.Commit(0);

            Hash256 rootHash = stateTree.RootHash;
            stateTree.RootHash = null;

            stateTree.RootHash = rootHash;
            stateTree.Get(TestItem.AddressA);
            account = account.WithChangedBalance(2);
            stateTree.Set(TestItem.AddressA, account);
            stateTree.Commit(0);

            Assert.That(db.Keys.Count, Is.EqualTo(2));
        }

        [TestCase(true, false)]
        [TestCase(false, true)]
        public void Commit_with_skip_root_should_skip_root(bool skipRoot, bool hasRoot)
        {
            MemDb db = new();
            TrieStore trieStore = new TrieStore(db, LimboLogs.Instance);
            Account account = new(1);

            StateTree stateTree = new(trieStore, LimboLogs.Instance);
            stateTree.Set(TestItem.AddressA, account);
            stateTree.UpdateRootHash();
            Hash256 stateRoot = stateTree.RootHash;
            stateTree.Commit(0, skipRoot);

            if (hasRoot)
            {
                trieStore.LoadRlp(null, TreePath.Empty, stateRoot).Length.Should().BeGreaterThan(0);
            }
            else
            {
                trieStore.Invoking(ts => ts.LoadRlp(null, TreePath.Empty, stateRoot)).Should().Throw<TrieException>();
            }
        }
    }
}
