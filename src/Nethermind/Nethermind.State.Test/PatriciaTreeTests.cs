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
    [TestFixture, Parallelizable(ParallelScope.All)]
    public class PatriciaTreeTests
    {

        [Test]
        public void Something()
        {
            AccountDecoder accountDecoder = new AccountDecoder();

            PatriciaTree tree1 = new PatriciaTree(new MemDb());
            PatriciaTree tree2 = new PatriciaTree(new MemDb());

            Span<byte> key1 = stackalloc byte[52];
            Span<byte> key2 = stackalloc byte[32];

            Account[] accounts =
            {
                new Account(1),
                new Account(2),
                new Account(3),
                new Account(4),
                new Account(5),
            };

            TestItem.AddressA.Bytes.CopyTo(key1);
            KeccakHash.ComputeHashBytesToSpan(TestItem.AddressB.Bytes, key1.Slice(20));
            KeccakHash.ComputeHashBytesToSpan(TestItem.AddressB.Bytes, key2);
            tree1.Set(key1, accountDecoder.Encode(accounts[0]));
            tree2.Set(key2, accountDecoder.Encode(accounts[0]));

            TestItem.AddressA.Bytes.CopyTo(key1);
            KeccakHash.ComputeHashBytesToSpan(TestItem.AddressC.Bytes, key1.Slice(20));
            KeccakHash.ComputeHashBytesToSpan(TestItem.AddressC.Bytes, key2);
            tree1.Set(key1, accountDecoder.Encode(accounts[1]));
            tree2.Set(key2, accountDecoder.Encode(accounts[1]));

            TestItem.AddressA.Bytes.CopyTo(key1);
            KeccakHash.ComputeHashBytesToSpan(TestItem.AddressD.Bytes, key1.Slice(20));
            KeccakHash.ComputeHashBytesToSpan(TestItem.AddressB.Bytes, key2);
            tree1.Set(key1, accountDecoder.Encode(accounts[2]));
            tree2.Set(key2, accountDecoder.Encode(accounts[2]));

            TestItem.AddressA.Bytes.CopyTo(key1);
            KeccakHash.ComputeHashBytesToSpan(TestItem.AddressE.Bytes, key1.Slice(20));
            KeccakHash.ComputeHashBytesToSpan(TestItem.AddressB.Bytes, key2);
            tree1.Set(key1, accountDecoder.Encode(accounts[3]));
            tree2.Set(key2, accountDecoder.Encode(accounts[3]));

            TestItem.AddressA.Bytes.CopyTo(key1);
            KeccakHash.ComputeHashBytesToSpan(TestItem.AddressF.Bytes, key1.Slice(20));
            KeccakHash.ComputeHashBytesToSpan(TestItem.AddressB.Bytes, key2);
            tree1.Set(key1, accountDecoder.Encode(accounts[4]));
            tree2.Set(key2, accountDecoder.Encode(accounts[4]));

            tree1.Commit(0);
            tree2.Commit(0);

            Console.WriteLine(string.Join(", ", tree1.RootHash));
            Console.WriteLine(string.Join(", ", tree2.RootHash));
        }


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
            Assert.AreEqual((UInt256)2, accountRestored.Balance);
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
            Assert.AreEqual((UInt256)2, accountRestored.Balance);
        }

        [Test]
        public void Create_commit_reset_change_balance_get()
        {
            MemDb db = new();
            Account account = new(1);
            StateTree stateTree = new(new TrieStore(db, LimboLogs.Instance), LimboLogs.Instance);
            stateTree.Set(TestItem.AddressA, account);
            stateTree.Commit(0);

            Keccak rootHash = stateTree.RootHash;
            stateTree.RootHash = null;

            stateTree.RootHash = rootHash;
            stateTree.Get(TestItem.AddressA);
            account = account.WithChangedBalance(2);
            stateTree.Set(TestItem.AddressA, account);
            stateTree.Commit(0);

            Assert.AreEqual(2, db.Keys.Count);
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
            Keccak stateRoot = stateTree.RootHash;
            stateTree.Commit(0, skipRoot);

            if (hasRoot)
            {
                trieStore.LoadRlp(stateRoot).Length.Should().BeGreaterThan(0);
            }
            else
            {
                trieStore.Invoking(ts => ts.LoadRlp(stateRoot)).Should().Throw<TrieException>();
            }
        }
    }
}
