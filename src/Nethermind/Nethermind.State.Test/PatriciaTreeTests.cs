// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
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
    [TestFixture(true)]
    [TestFixture(false)]
    [Parallelizable(ParallelScope.All)]
    public class PatriciaTreeTests(bool useFullTrieStore)
    {
        [Test]
        public void Create_commit_change_balance_get()
        {
            Account account = new(1);
            StateTree stateTree = new(CreateTrieStore(), LimboLogs.Instance);
            stateTree.Set(TestItem.AddressA, account);
            stateTree.Commit();

            account = account.WithChangedBalance(2);
            stateTree.Set(TestItem.AddressA, account);
            stateTree.Commit();

            Account accountRestored = stateTree.Get(TestItem.AddressA);
            Assert.That(accountRestored.Balance, Is.EqualTo((UInt256)2));
        }

        [Test]
        public void Create_create_commit_change_balance_get()
        {
            Account account = new(1);
            StateTree stateTree = new(CreateTrieStore(), LimboLogs.Instance);
            stateTree.Set(TestItem.AddressA, account);
            stateTree.Set(TestItem.AddressB, account);
            stateTree.Commit();

            account = account.WithChangedBalance(2);
            stateTree.Set(TestItem.AddressA, account);
            stateTree.Commit();

            Account accountRestored = stateTree.Get(TestItem.AddressA);
            Assert.That(accountRestored.Balance, Is.EqualTo((UInt256)2));
        }

        [Test]
        public void Create_commit_reset_change_balance_get()
        {
            MemDb db = new();
            Account account = new(1);
            StateTree stateTree = new(CreateTrieStore(db), LimboLogs.Instance);
            stateTree.Set(TestItem.AddressA, account);
            stateTree.Commit();

            Hash256 rootHash = stateTree.RootHash;
            stateTree.RootHash = null;

            stateTree.RootHash = rootHash;
            stateTree.Get(TestItem.AddressA);
            account = account.WithChangedBalance(2);
            stateTree.Set(TestItem.AddressA, account);
            stateTree.Commit();

            Assert.That(db.Keys.Count, Is.EqualTo(2));
        }

        [TestCase(true, false)]
        [TestCase(false, true)]
        public void Commit_with_skip_root_should_skip_root(bool skipRoot, bool hasRoot)
        {
            IScopedTrieStore trieStore = CreateTrieStore();
            Account account = new(1);

            StateTree stateTree = new(trieStore, LimboLogs.Instance);
            stateTree.Set(TestItem.AddressA, account);
            stateTree.UpdateRootHash();
            Hash256 stateRoot = stateTree.RootHash;
            stateTree.Commit(skipRoot);

            if (hasRoot)
            {
                trieStore.LoadRlp(TreePath.Empty, stateRoot).Length.Should().BeGreaterThan(0);
            }
            else
            {
                trieStore.Invoking(ts => ts.LoadRlp(TreePath.Empty, stateRoot)).Should().Throw<TrieException>();
            }
        }

        private IScopedTrieStore CreateTrieStore(IDb db = null)
        {
            db ??= new MemDb();
            return useFullTrieStore
                ? TestTrieStoreFactory.Build(db, LimboLogs.Instance).GetTrieStore(null)
                : new RawScopedTrieStore(new NodeStorage(db), null);
        }
    }
}
