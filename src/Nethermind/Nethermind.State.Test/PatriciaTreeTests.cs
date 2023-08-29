// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Store.Test
{
    [Parallelizable(ParallelScope.All)]
    [TestFixture(TrieNodeResolverCapability.Hash)]
    [TestFixture(TrieNodeResolverCapability.Path)]
    public class PatriciaTreeTests
    {
        private readonly TrieNodeResolverCapability _resolverCapability;

        public PatriciaTreeTests(TrieNodeResolverCapability resolverCapability)
        {
            _resolverCapability = resolverCapability;
        }

        [Test]
        public void Create_commit_change_balance_get()
        {
            Account account = new(1);
            IStateTree stateTree = _resolverCapability.CreateStateStore();
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
            IStateTree stateTree = _resolverCapability.CreateStateStore();
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
            MemColumnsDb<StateColumns> db = new();
            Account account = new(1);
            IStateTree stateTree = _resolverCapability.CreateStateStore(db, LimboLogs.Instance);
            stateTree.Set(TestItem.AddressA, account);
            stateTree.Commit(0);

            Keccak rootHash = stateTree.RootHash;
            stateTree.RootHash = null;

            stateTree.RootHash = rootHash;
            stateTree.Get(TestItem.AddressA);
            account = account.WithChangedBalance(2);
            stateTree.Set(TestItem.AddressA, account);
            stateTree.Commit(0);

            Account a1 = stateTree.Get(TestItem.AddressA, rootHash);
            Account a2 = stateTree.Get(TestItem.AddressA);

            Assert.That(a1.Balance, Is.EqualTo((UInt256)1));
            Assert.That(a2.Balance, Is.EqualTo((UInt256)2));
        }

        [TestCase(true, false)]
        [TestCase(false, true)]
        public void Commit_with_skip_root_should_skip_root(bool skipRoot, bool hasRoot)
        {
            MemColumnsDb<StateColumns> db = new();
            ITrieStore trieStore = _resolverCapability.CreateTrieStore(db, LimboLogs.Instance);
            Account account = new(1);

            ITrieStore storagerTrieStore = _resolverCapability.CreateTrieStore(db, LimboLogs.Instance);

            IStateTree stateTree = _resolverCapability.CreateStateStore(trieStore, LimboLogs.Instance);
            stateTree.Set(TestItem.AddressA, account);
            stateTree.UpdateRootHash();
            Keccak stateRoot = stateTree.RootHash;
            stateTree.Commit(0, skipRoot);

            switch (_resolverCapability)
            {
                case TrieNodeResolverCapability.Hash:
                    if (hasRoot)
                        trieStore.LoadRlp(stateRoot).Length.Should().BeGreaterThan(0);
                    else
                        trieStore.Invoking(ts => ts.LoadRlp(stateRoot)).Should().Throw<TrieException>();
                    break;
                case TrieNodeResolverCapability.Path:
                    if (hasRoot)
                        trieStore.LoadRlp(Array.Empty<byte>(), stateRoot).Length.Should().BeGreaterThan(0);
                    else
                        trieStore.Invoking(ts => ts.LoadRlp(Array.Empty<byte>(), stateRoot)).Should().Throw<TrieException>();
                    break;
                default:
                    break;
            }
        }
    }
}
