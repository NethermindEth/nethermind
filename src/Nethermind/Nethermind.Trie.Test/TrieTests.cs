// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Trie.Test
{
    [TestFixture]
    public class TrieTests
    {
        private ILogger _logger;
        private ILogManager _logManager;
        private Random _random = new();

        [SetUp]
        public void SetUp()
        {
            _logManager = LimboLogs.Instance;
            _logger = _logManager.GetClassLogger();
        }

        [TearDown]
        public void TearDown()
        {
        }

        private static readonly byte[] _longLeaf1
            = Bytes.FromHexString("0000000000000000000000000000000000000000000000000000000000000000000000000000000001");

        private static readonly byte[] _longLeaf2
            = Bytes.FromHexString("0000000000000000000000000000000000000000000000000000000000000000000000000000000002");

        private static readonly byte[] _longLeaf3
            = Bytes.FromHexString("0000000000000000000000000000000000000000000000000000000000000000000000000000000003");

        private static readonly byte[] _keyA = Bytes.FromHexString("00000000000aa");
        private static readonly byte[] _keyB = Bytes.FromHexString("00000000000bb");
        private static readonly byte[] _keyC = Bytes.FromHexString("00000000001aa");
        private static readonly byte[] _keyD = Bytes.FromHexString("00000000001bb");

        [Test]
        public void Single_leaf()
        {
            MemDb memDb = new();
            using TrieStore trieStore = TestTrieStoreFactory.Build(memDb, Prune.WhenCacheReaches(1.MB()), Persist.EveryBlock, _logManager);
            PatriciaTree patriciaTree = new(trieStore, _logManager);
            patriciaTree.Set(_keyA, _longLeaf1);
            trieStore.CommitPatriciaTrie(0, patriciaTree);

            // leaf (root)
            memDb.Keys.Should().HaveCount(1);
        }

        [Test]
        public void Single_leaf_update_same_block()
        {
            MemDb memDb = new();
            using TrieStore trieStore = TestTrieStoreFactory.Build(memDb, Prune.WhenCacheReaches(1.MB()), Persist.EveryBlock, _logManager);
            PatriciaTree patriciaTree = new(trieStore, _logManager);
            patriciaTree.Set(_keyA, _longLeaf1);
            patriciaTree.Set(_keyA, _longLeaf2);
            trieStore.CommitPatriciaTrie(0, patriciaTree);

            // leaf (root)
            memDb.Keys.Should().HaveCount(1);

            PatriciaTree checkTree = CreateCheckTree(memDb, patriciaTree);
            checkTree.Get(_keyA).ToArray().Should().NotBeEquivalentTo(_longLeaf1);
            checkTree.Get(_keyA).ToArray().Should().BeEquivalentTo(_longLeaf2);
        }

        [Test]
        public void Single_leaf_update_next_blocks()
        {
            MemDb memDb = new();
            using TrieStore trieStore = TestTrieStoreFactory.Build(memDb, Prune.WhenCacheReaches(1.MB()), Persist.EveryBlock, _logManager);
            PatriciaTree patriciaTree = new(trieStore, _logManager);
            patriciaTree.Set(_keyA, _longLeaf1);
            trieStore.CommitPatriciaTrie(0, patriciaTree);
            patriciaTree.Set(_keyA, _longLeaf2);
            trieStore.CommitPatriciaTrie(1, patriciaTree);
            patriciaTree.UpdateRootHash();

            // leaf (root)
            memDb.Keys.Should().HaveCount(2);

            PatriciaTree checkTree = CreateCheckTree(memDb, patriciaTree);
            checkTree.Get(_keyA).ToArray().Should().NotBeEquivalentTo(_longLeaf1);
            checkTree.Get(_keyA).ToArray().Should().BeEquivalentTo(_longLeaf2);
        }

        [Test]
        public void Single_leaf_delete_same_block()
        {
            MemDb memDb = new();
            using TrieStore trieStore = TestTrieStoreFactory.Build(memDb, Prune.WhenCacheReaches(1.MB()), No.Persistence, _logManager);
            PatriciaTree patriciaTree = new(trieStore, _logManager);
            patriciaTree.Set(_keyA, _longLeaf1);
            patriciaTree.Set(_keyA, []);
            trieStore.CommitPatriciaTrie(0, patriciaTree);

            // leaf (root)
            memDb.Keys.Should().HaveCount(0);

            PatriciaTree checkTree = CreateCheckTree(memDb, patriciaTree);
            checkTree.Get(_keyA).ToArray().Should().BeEmpty();
        }

        [Test]
        public void Single_leaf_delete_next_block()
        {
            MemDb memDb = new();
            using TrieStore trieStore = TestTrieStoreFactory.Build(memDb, Prune.WhenCacheReaches(1.MB()), Persist.EveryBlock, _logManager);
            PatriciaTree patriciaTree = new(trieStore, _logManager);
            patriciaTree.Set(_keyA, _longLeaf1);
            trieStore.CommitPatriciaTrie(0, patriciaTree);
            patriciaTree.Set(_keyA, []);
            trieStore.CommitPatriciaTrie(1, patriciaTree);
            patriciaTree.UpdateRootHash();

            // leaf (root)
            memDb.Keys.Should().HaveCount(1);

            PatriciaTree checkTree = CreateCheckTree(memDb, patriciaTree);
            checkTree.Get(_keyA).ToArray().Should().BeEmpty();
        }

        [Test]
        public void Single_leaf_and_keep_for_multiple_dispatches_then_delete()
        {
            MemDb memDb = new();
            using TrieStore trieStore = TestTrieStoreFactory.Build(memDb, Prune.WhenCacheReaches(1.MB()), new ConstantInterval(4), LimboLogs.Instance);
            PatriciaTree patriciaTree = new(trieStore, _logManager);
            trieStore.CommitPatriciaTrie(0, patriciaTree);
            trieStore.CommitPatriciaTrie(1, patriciaTree);
            trieStore.CommitPatriciaTrie(2, patriciaTree);
            patriciaTree.Set(_keyA, _longLeaf1);
            trieStore.CommitPatriciaTrie(3, patriciaTree);
            trieStore.CommitPatriciaTrie(4, patriciaTree);
            patriciaTree.Set(_keyA, []);
            trieStore.CommitPatriciaTrie(5, patriciaTree);
            patriciaTree.Set(_keyB, _longLeaf2);
            trieStore.CommitPatriciaTrie(6, patriciaTree);
            trieStore.CommitPatriciaTrie(7, patriciaTree);
            trieStore.CommitPatriciaTrie(8, patriciaTree);
            trieStore.CommitPatriciaTrie(9, patriciaTree);
            trieStore.CommitPatriciaTrie(10, patriciaTree);
            trieStore.CommitPatriciaTrie(11, patriciaTree);
            patriciaTree.Set(_keyB, []);
            trieStore.CommitPatriciaTrie(12, patriciaTree);
            trieStore.CommitPatriciaTrie(13, patriciaTree);
            patriciaTree.UpdateRootHash();

            // leaf (root)
            memDb.Keys.Should().HaveCount(2);

            PatriciaTree checkTree = CreateCheckTree(memDb, patriciaTree);
            checkTree.Get(_keyA).ToArray().Should().BeEmpty();
            checkTree.Get(_keyB).ToArray().Should().BeEmpty();
        }

        [Test]
        public void Branch_with_branch_and_leaf()
        {
            MemDb memDb = new();
            using TrieStore trieStore = TestTrieStoreFactory.Build(memDb, Prune.WhenCacheReaches(1.MB()), Persist.EveryBlock, _logManager);
            PatriciaTree patriciaTree = new(trieStore, _logManager);
            patriciaTree.Set(_keyA, _longLeaf1);
            patriciaTree.Set(_keyB, _longLeaf1);
            patriciaTree.Set(_keyC, _longLeaf1);
            trieStore.CommitPatriciaTrie(0, patriciaTree);

            // leaf (root)
            memDb.Keys.Should().HaveCount(6);
            PatriciaTree checkTree = CreateCheckTree(memDb, patriciaTree);
            checkTree.Get(_keyA).ToArray().Should().BeEquivalentTo(_longLeaf1);
            checkTree.Get(_keyB).ToArray().Should().BeEquivalentTo(_longLeaf1);
            checkTree.Get(_keyC).ToArray().Should().BeEquivalentTo(_longLeaf1);
        }

        [Test]
        public void GetBranchNodesWithPartialPath()
        {

            byte[] _keysA = Bytes.FromHexString("00000000000000aa");
            byte[] _keysB = Bytes.FromHexString("00000000000000bb");
            byte[] _keysC = Bytes.FromHexString("00000000000011aa");

            byte[] branchNodeKey1 = Bytes.FromHexString("000000000000");
            byte[] branchNodeValue1 =
                Bytes.FromHexString(
                    "f851a0fc3531d07692f61463485d46bac9ad2785c14fc66929d156df6ffc8e7a298f0da0596473298079c2907c4de5a8646467ebb46d7b5066bce4dc0f653380fe958804808080808080808080808080808080");
            // geth output: f851a0fc3531d07692f61463485d46bac9ad2785c14fc66929d156df6ffc8e7a298f0da0596473298079c2907c4de5a8646467ebb46d7b5066bce4dc0f653380fe958804808080808080808080808080808080

            byte[] rootNodeHash =
                Bytes.FromHexString(
                    "e98700000000000000a0651f4a047389788364f9da07e907614238cbbe902d722c9b3333a4300308a5ae");

            MemDb memDb = new();
            TrieStore trieStore = TestTrieStoreFactory.Build(memDb, Prune.WhenCacheReaches(1.MB()), Persist.EveryBlock, _logManager);
            PatriciaTree patriciaTree = new(trieStore, _logManager);
            patriciaTree.Set(_keysA, _longLeaf1);
            patriciaTree.Set(_keysB, _longLeaf1);
            patriciaTree.Set(_keysC, _longLeaf1);
            trieStore.CommitPatriciaTrie(0, patriciaTree);

            PatriciaTree checkTree = CreateCheckTree(memDb, patriciaTree);

            byte[] emptyByte = [];
            byte[] emptyByteCompactEncoded = { 0 };

            checkTree.GetNodeByKey(emptyByte, patriciaTree.RootHash).Should().BeEquivalentTo(rootNodeHash);
            checkTree.GetNodeByKey(Nibbles.CompactToHexEncode(emptyByteCompactEncoded), patriciaTree.RootHash).Should().BeEquivalentTo(rootNodeHash);

            checkTree.GetNodeByKey(branchNodeKey1, patriciaTree.RootHash).Should().BeEquivalentTo(branchNodeValue1);
        }

        // [Test]
        // public void When_an_inlined_leaf_is_cloned_and_the_extended_version_is_no_longer_inlined()
        // {
        //     throw new NotImplementedException();
        // }
        //
        // [Test]
        // public void When_a_node_is_loaded_from_the_DB_as_unknown_and_unreferenced()
        // {
        //     throw new NotImplementedException();
        // }

        [Test]
        public void Branch_with_branch_and_leaf_then_deleted()
        {
            MemDb memDb = new();
            using TrieStore trieStore = TestTrieStoreFactory.Build(memDb, Prune.WhenCacheReaches(1.MB()), Persist.EveryBlock, _logManager);
            PatriciaTree patriciaTree = new(trieStore, _logManager);
            patriciaTree.Set(_keyA, _longLeaf1);
            patriciaTree.Set(_keyB, _longLeaf1);
            patriciaTree.Set(_keyC, _longLeaf1);
            trieStore.CommitPatriciaTrie(0, patriciaTree);
            patriciaTree.Set(_keyA, []);
            patriciaTree.Set(_keyB, []);
            patriciaTree.Set(_keyC, []);
            trieStore.CommitPatriciaTrie(1, patriciaTree);
            patriciaTree.UpdateRootHash();

            // leaf (root)
            memDb.Keys.Should().HaveCount(6);
            PatriciaTree checkTree = CreateCheckTree(memDb, patriciaTree);
            checkTree.Get(_keyA).ToArray().Should().BeEmpty();
            checkTree.Get(_keyB).ToArray().Should().BeEmpty();
            checkTree.Get(_keyC).ToArray().Should().BeEmpty();
        }

        public void Test_add_many(int i)
        {
            MemDb memDb = new();
            using TrieStore trieStore = TestTrieStoreFactory.Build(memDb, new MemoryLimit(128.MB()), Persist.EveryBlock, _logManager);
            PatriciaTree patriciaTree = new(trieStore.GetTrieStore(null), Keccak.EmptyTreeHash, true, true, _logManager);

            for (int j = 0; j < i; j++)
            {
                Hash256 key = TestItem.Keccaks[j];
                byte[] value = TestItem.GenerateIndexedAccountRlp(j);
                patriciaTree.Set(key.Bytes, value);
            }

            trieStore.CommitPatriciaTrie(0, patriciaTree);

            patriciaTree.UpdateRootHash();

            PatriciaTree checkTree = CreateCheckTree(memDb, patriciaTree);
            for (int j = 0; j < i; j++)
            {
                Hash256 key = TestItem.Keccaks[j];
                byte[] value = TestItem.GenerateIndexedAccountRlp(j);
                checkTree.Get(key.Bytes).ToArray().Should().BeEquivalentTo(value, $@"{i} {j}");
            }
        }

        public void Test_try_delete_and_read_missing_nodes(int i)
        {
            MemDb memDb = new();
            using TrieStore trieStore = TestTrieStoreFactory.Build(memDb, new MemoryLimit(128.MB()), Persist.EveryBlock, _logManager);
            PatriciaTree patriciaTree = new(trieStore.GetTrieStore(null), Keccak.EmptyTreeHash, true, true, _logManager);

            for (int j = 0; j < i; j++)
            {
                Hash256 key = TestItem.Keccaks[j];
                byte[] value = TestItem.GenerateIndexedAccountRlp(j);
                patriciaTree.Set(key.Bytes, value);
            }

            // delete missing
            for (int j = 0; j < i; j++)
            {
                Hash256 key = TestItem.Keccaks[j + 100];
                patriciaTree.Set(key.Bytes, []);
            }

            trieStore.CommitPatriciaTrie(0, patriciaTree);
            patriciaTree.UpdateRootHash();

            PatriciaTree checkTree = CreateCheckTree(memDb, patriciaTree);

            // confirm nothing deleted
            for (int j = 0; j < i; j++)
            {
                Hash256 key = TestItem.Keccaks[j];
                byte[] value = TestItem.GenerateIndexedAccountRlp(j);
                checkTree.Get(key.Bytes).ToArray().Should().BeEquivalentTo(value, $@"{i} {j}");
            }

            // read missing
            for (int j = 0; j < i; j++)
            {
                Hash256 key = TestItem.Keccaks[j + 100];
                checkTree.Get(key.Bytes).ToArray().Should().BeEmpty();
            }
        }

        public void Test_update_many(int i)
        {
            MemDb memDb = new();
            using TrieStore trieStore = TestTrieStoreFactory.Build(memDb, new MemoryLimit(128.MB()), Persist.EveryBlock, _logManager);
            PatriciaTree patriciaTree = new(trieStore, _logManager);

            for (int j = 0; j < i; j++)
            {
                Hash256 key = TestItem.Keccaks[j];
                byte[] value = TestItem.GenerateIndexedAccountRlp(j);
                patriciaTree.Set(key.Bytes, value);
            }

            for (int j = 0; j < i; j++)
            {
                Hash256 key = TestItem.Keccaks[j];
                byte[] value = TestItem.GenerateIndexedAccountRlp(j + 1);
                patriciaTree.Set(key.Bytes, value);
            }

            trieStore.CommitPatriciaTrie(0, patriciaTree);
            patriciaTree.UpdateRootHash();

            PatriciaTree checkTree = CreateCheckTree(memDb, patriciaTree);
            for (int j = 0; j < i; j++)
            {
                Hash256 key = TestItem.Keccaks[j];
                byte[] value = TestItem.GenerateIndexedAccountRlp(j + 1);
                checkTree.Get(key.Bytes).ToArray().Should().BeEquivalentTo(value, $@"{i} {j}");
            }
        }

        public void Test_update_many_next_block(int i)
        {
            MemDb memDb = new();
            using TrieStore trieStore = TestTrieStoreFactory.Build(memDb, new MemoryLimit(128.MB()), Persist.EveryBlock, _logManager);
            PatriciaTree patriciaTree = new(trieStore, _logManager);

            for (int j = 0; j < i; j++)
            {
                Hash256 key = TestItem.Keccaks[j];
                byte[] value = TestItem.GenerateIndexedAccountRlp(j);
                patriciaTree.Set(key.Bytes, value);
            }

            trieStore.CommitPatriciaTrie(0, patriciaTree);

            for (int j = 0; j < i; j++)
            {
                Hash256 key = TestItem.Keccaks[j];
                byte[] value = TestItem.GenerateIndexedAccountRlp(j + 1);
                patriciaTree.Set(key.Bytes, value);
                _logger.Trace($"Setting {key.Bytes.ToHexString()} = {value.ToHexString()}");
            }

            trieStore.CommitPatriciaTrie(1, patriciaTree);
            patriciaTree.UpdateRootHash();

            PatriciaTree checkTree = CreateCheckTree(memDb, patriciaTree);
            for (int j = 0; j < i; j++)
            {
                Hash256 key = TestItem.Keccaks[j];
                byte[] value = TestItem.GenerateIndexedAccountRlp(j + 1);

                _logger.Trace($"Checking {key.Bytes.ToHexString()} = {value.ToHexString()}");
                checkTree.Get(key.Bytes).ToArray().Should().BeEquivalentTo(value, $@"{i} {j}");
            }
        }

        public void Test_add_and_delete_many_same_block(int i)
        {
            MemDb memDb = new();
            using TrieStore trieStore = TestTrieStoreFactory.Build(memDb, new MemoryLimit(128.MB()), Persist.EveryBlock, _logManager);
            PatriciaTree patriciaTree = new(trieStore, _logManager);

            for (int j = 0; j < i; j++)
            {
                _logger.Trace($"  set {j}");
                Hash256 key = TestItem.Keccaks[j];
                byte[] value = TestItem.GenerateIndexedAccountRlp(j);
                patriciaTree.Set(key.Bytes, value);
            }

            for (int j = 0; j < i; j++)
            {
                _logger.Trace($"  delete {j}");
                Hash256 key = TestItem.Keccaks[j];
                patriciaTree.Set(key.Bytes, []);
            }

            trieStore.CommitPatriciaTrie(0, patriciaTree);
            patriciaTree.UpdateRootHash();

            PatriciaTree checkTree = CreateCheckTree(memDb, patriciaTree);
            for (int j = 0; j < i; j++)
            {
                Hash256 key = TestItem.Keccaks[j];
                checkTree.Get(key.Bytes).ToArray().Should().BeEmpty($"{i} {j}");
            }
        }

        public void Test_add_and_delete_many_next_block(int i)
        {
            MemDb memDb = new();
            using TrieStore trieStore = TestTrieStoreFactory.Build(memDb, new MemoryLimit(128.MB()), Persist.EveryBlock, _logManager);
            PatriciaTree patriciaTree = new(trieStore, _logManager);

            for (int j = 0; j < i; j++)
            {
                Hash256 key = TestItem.Keccaks[j];
                byte[] value = TestItem.GenerateIndexedAccountRlp(j);
                patriciaTree.Set(key.Bytes, value);
            }

            trieStore.CommitPatriciaTrie(0, patriciaTree);

            for (int j = 0; j < i; j++)
            {
                Hash256 key = TestItem.Keccaks[j];
                patriciaTree.Set(key.Bytes, []);
            }

            trieStore.CommitPatriciaTrie(1, patriciaTree);
            patriciaTree.UpdateRootHash();

            PatriciaTree checkTree = CreateCheckTree(memDb, patriciaTree);
            for (int j = 0; j < i; j++)
            {
                Hash256 key = TestItem.Keccaks[j];
                checkTree.Get(key.Bytes).ToArray().Should().BeEmpty($"{i} {j}");
            }
        }

        [Test]
        public void Big_test()
        {
            // there was a case that was failing only at iteration 85 (before you change it to a smaller number)

            for (int i = 0; i < 100; i++)
            {
                _logger.Trace(i.ToString());
                Test_add_many(i);
                Test_update_many(i);
                Test_update_many_next_block(i);
                Test_add_and_delete_many_same_block(i);
                Test_add_and_delete_many_next_block(i);
                Test_try_delete_and_read_missing_nodes(i);
            }
        }

        [Test]
        public void Two_branches_exactly_same_leaf()
        {
            MemDb memDb = new();
            using TrieStore trieStore = TestTrieStoreFactory.Build(memDb, Prune.WhenCacheReaches(1.MB()), Persist.EveryBlock, _logManager);
            PatriciaTree patriciaTree = new(trieStore, _logManager);
            patriciaTree.Set(_keyA, _longLeaf1);
            patriciaTree.Set(_keyB, _longLeaf1);
            patriciaTree.Set(_keyC, _longLeaf1);
            patriciaTree.Set(_keyD, _longLeaf1);
            trieStore.CommitPatriciaTrie(0, patriciaTree);

            // leaf (root)
            memDb.Keys.Should().HaveCount(8);
            PatriciaTree checkTree = CreateCheckTree(memDb, patriciaTree);
            checkTree.Get(_keyA).ToArray().Should().BeEquivalentTo(_longLeaf1);
            checkTree.Get(_keyB).ToArray().Should().BeEquivalentTo(_longLeaf1);
            checkTree.Get(_keyC).ToArray().Should().BeEquivalentTo(_longLeaf1);
            checkTree.Get(_keyD).ToArray().Should().BeEquivalentTo(_longLeaf1);
        }

        [Test]
        public void Two_branches_exactly_same_leaf_then_one_removed()
        {
            MemDb memDb = new();
            using TrieStore trieStore = TestTrieStoreFactory.Build(memDb,
                Prune.WhenCacheReaches(1.MB()),
                Persist.EveryBlock,
                LimboLogs.Instance);
            PatriciaTree patriciaTree = new(trieStore, _logManager);
            patriciaTree.Set(_keyA, _longLeaf1);
            patriciaTree.Set(_keyB, _longLeaf1);
            patriciaTree.Set(_keyC, _longLeaf1);
            patriciaTree.Set(_keyD, _longLeaf1);
            patriciaTree.Set(_keyA, []);
            trieStore.CommitPatriciaTrie(0, patriciaTree);

            // leaf (root)
            memDb.Keys.Should().HaveCount(6);
            PatriciaTree checkTree = CreateCheckTree(memDb, patriciaTree);
            checkTree.Get(_keyA).ToArray().Should().BeEmpty();
            checkTree.Get(_keyB).ToArray().Should().BeEquivalentTo(_longLeaf1);
            checkTree.Get(_keyC).ToArray().Should().BeEquivalentTo(_longLeaf1);
            checkTree.Get(_keyD).ToArray().Should().BeEquivalentTo(_longLeaf1);
        }

        private static PatriciaTree CreateCheckTree(MemDb memDb, PatriciaTree patriciaTree)
        {
            PatriciaTree checkTree = new(memDb);
            checkTree.RootHash = patriciaTree.RootHash;
            return checkTree;
        }

        [Test]
        public void Extension_with_branch_with_two_different_children()
        {
            MemDb memDb = new();
            using TrieStore trieStore = TestTrieStoreFactory.Build(memDb, Prune.WhenCacheReaches(1.MB()), Persist.EveryBlock, _logManager);
            PatriciaTree patriciaTree = new(trieStore, _logManager);
            patriciaTree.Set(_keyA, _longLeaf1);
            patriciaTree.Set(_keyB, _longLeaf2);
            trieStore.CommitPatriciaTrie(0, patriciaTree);
            memDb.Keys.Should().HaveCount(4);
            PatriciaTree checkTree = CreateCheckTree(memDb, patriciaTree);
            checkTree.Get(_keyA).ToArray().Should().BeEquivalentTo(_longLeaf1);
            checkTree.Get(_keyB).ToArray().Should().BeEquivalentTo(_longLeaf2);
        }

        [Test]
        public void Extension_with_branch_with_two_same_children()
        {
            MemDb memDb = new();
            using TrieStore trieStore = TestTrieStoreFactory.Build(memDb, Prune.WhenCacheReaches(1.MB()), Persist.EveryBlock, _logManager);
            PatriciaTree patriciaTree = new(trieStore, _logManager);
            patriciaTree.Set(_keyA, _longLeaf1);
            patriciaTree.Set(_keyB, _longLeaf1);
            trieStore.CommitPatriciaTrie(0, patriciaTree);
            memDb.Keys.Should().HaveCount(4);
            PatriciaTree checkTree = CreateCheckTree(memDb, patriciaTree);
            checkTree.Get(_keyA).ToArray().Should().BeEquivalentTo(_longLeaf1);
            checkTree.Get(_keyB).ToArray().Should().BeEquivalentTo(_longLeaf1);
        }

        [Test]
        public void When_branch_with_two_different_children_change_one_and_change_back_next_block()
        {
            MemDb memDb = new();
            using TrieStore trieStore = TestTrieStoreFactory.Build(memDb, Prune.WhenCacheReaches(1.MB()), Persist.EveryBlock, _logManager);
            PatriciaTree patriciaTree = new(trieStore, _logManager);
            patriciaTree.Set(_keyA, _longLeaf1);
            patriciaTree.Set(_keyB, _longLeaf2);
            patriciaTree.UpdateRootHash();
            trieStore.CommitPatriciaTrie(0, patriciaTree);
            patriciaTree.Set(_keyA, _longLeaf3);
            patriciaTree.Set(_keyA, _longLeaf1);
            patriciaTree.UpdateRootHash();
            trieStore.CommitPatriciaTrie(1, patriciaTree);

            // extension
            // branch
            // leaf x 2
            memDb.Keys.Should().HaveCount(4);
        }

        [Test]
        public void When_branch_with_two_same_children_change_one_and_change_back_next_block()
        {
            MemDb memDb = new();
            using TrieStore trieStore = TestTrieStoreFactory.Build(memDb, Prune.WhenCacheReaches(1.MB()), Persist.EveryBlock, _logManager);
            PatriciaTree patriciaTree = new(trieStore, _logManager);
            patriciaTree.Set(_keyA, _longLeaf1);
            patriciaTree.Set(_keyB, _longLeaf1);
            patriciaTree.UpdateRootHash();
            trieStore.CommitPatriciaTrie(0, patriciaTree);
            patriciaTree.Set(_keyA, _longLeaf3);
            patriciaTree.Set(_keyA, _longLeaf1);
            patriciaTree.UpdateRootHash();
            trieStore.CommitPatriciaTrie(1, patriciaTree);

            memDb.Keys.Should().HaveCount(4);
            PatriciaTree checkTree = CreateCheckTree(memDb, patriciaTree);
            checkTree.Get(_keyA).ToArray().Should().BeEquivalentTo(_longLeaf1);
            checkTree.Get(_keyB).ToArray().Should().BeEquivalentTo(_longLeaf1);
        }

        [Test]
        public void Extension_branch_extension_and_leaf_then_branch_leaf_leaf()
        {
            /* R
               E - - - - - - - - - - - - - - -
               B B B B B B B B B B B B B B B B
               E L - - - - - - - - - - - - - -
               E - - - - - - - - - - - - - - -
               B B B B B B B B B B B B B B B B
               L L - - - - - - - - - - - - - - */

            byte[] key1 = Bytes.FromHexString("000000100000000aa");
            byte[] key2 = Bytes.FromHexString("000000100000000bb");
            byte[] key3 = Bytes.FromHexString("000000200000000cc");

            MemDb memDb = new();
            using TrieStore trieStore = TestTrieStoreFactory.Build(memDb, Prune.WhenCacheReaches(1.MB()), Persist.EveryBlock, _logManager);
            PatriciaTree patriciaTree = new(trieStore, _logManager);
            patriciaTree.Set(key1, _longLeaf1);
            patriciaTree.Set(key2, _longLeaf1);
            patriciaTree.Set(key3, _longLeaf1);
            patriciaTree.UpdateRootHash();
            trieStore.CommitPatriciaTrie(0, patriciaTree);

            memDb.Keys.Should().HaveCount(7);
            PatriciaTree checkTree = CreateCheckTree(memDb, patriciaTree);
            checkTree.Get(key1).ToArray().Should().BeEquivalentTo(_longLeaf1);
            checkTree.Get(key2).ToArray().Should().BeEquivalentTo(_longLeaf1);
            checkTree.Get(key3).ToArray().Should().BeEquivalentTo(_longLeaf1);
        }

        [Test]
        public void Connect_extension_with_extension()
        {
            /* to test this case we need something like this initially */
            /* R
               E - - - - - - - - - - - - - - -
               B B B B B B B B B B B B B B B B
               E L - - - - - - - - - - - - - -
               E - - - - - - - - - - - - - - -
               B B B B B B B B B B B B B B B B
               L L - - - - - - - - - - - - - - */

            /* then we delete the leaf (marked as X) */
            /* R
               B B B B B B B B B B B B B B B B
               E X - - - - - - - - - - - - - -
               E - - - - - - - - - - - - - - -
               B B B B B B B B B B B B B B B B
               L L - - - - - - - - - - - - - - */

            /* and we end up with an extended extension replacing what was previously a top-level branch*/
            /* R
               E
               E
               E - - - - - - - - - - - - - - -
               B B B B B B B B B B B B B B B B
               L L - - - - - - - - - - - - - - */

            byte[] key1 = Bytes.FromHexString("000000100000000aa");
            byte[] key2 = Bytes.FromHexString("000000100000000bb");
            byte[] key3 = Bytes.FromHexString("000000200000000cc");

            MemDb memDb = new();
            using TrieStore trieStore = TestTrieStoreFactory.Build(memDb, Prune.WhenCacheReaches(1.MB()), Persist.EveryBlock, _logManager);
            PatriciaTree patriciaTree = new(trieStore, _logManager);
            patriciaTree.Set(key1, _longLeaf1);
            patriciaTree.Set(key2, _longLeaf1);
            patriciaTree.Set(key3, _longLeaf1);
            patriciaTree.UpdateRootHash();
            trieStore.CommitPatriciaTrie(0, patriciaTree);
            patriciaTree.Set(key3, []);
            patriciaTree.UpdateRootHash();
            trieStore.CommitPatriciaTrie(1, patriciaTree);

            memDb.Keys.Should().HaveCount(8);
            PatriciaTree checkTree = CreateCheckTree(memDb, patriciaTree);
            checkTree.Get(key1).ToArray().Should().BeEquivalentTo(_longLeaf1);
            checkTree.Get(key2).ToArray().Should().BeEquivalentTo(_longLeaf1);
            checkTree.Get(key3).ToArray().Should().BeEmpty();
        }

        [Test]
        public void When_two_branches_with_two_same_children_change_one_and_change_back_next_block()
        {
            MemDb memDb = new();
            using TrieStore trieStore = TestTrieStoreFactory.Build(memDb, Prune.WhenCacheReaches(1.MB()), Persist.EveryBlock, _logManager);
            PatriciaTree patriciaTree = new(trieStore, _logManager);
            patriciaTree.Set(_keyA, _longLeaf1);
            patriciaTree.Set(_keyB, _longLeaf1);
            patriciaTree.Set(_keyC, _longLeaf1);
            patriciaTree.Set(_keyD, _longLeaf1);
            patriciaTree.UpdateRootHash();
            trieStore.CommitPatriciaTrie(0, patriciaTree);
            patriciaTree.Set(_keyA, _longLeaf3);
            patriciaTree.Set(_keyA, _longLeaf1);
            patriciaTree.UpdateRootHash();
            trieStore.CommitPatriciaTrie(1, patriciaTree);

            memDb.Keys.Should().HaveCount(8);
            PatriciaTree checkTree = CreateCheckTree(memDb, patriciaTree);
            checkTree.Get(_keyA).ToArray().Should().BeEquivalentTo(_longLeaf1);
            checkTree.Get(_keyB).ToArray().Should().BeEquivalentTo(_longLeaf1);
            checkTree.Get(_keyC).ToArray().Should().BeEquivalentTo(_longLeaf1);
            checkTree.Get(_keyD).ToArray().Should().BeEquivalentTo(_longLeaf1);
        }

        public record TrieStoreConfigurations(
            long dirtyNodeSize,
            int PersistEveryN,
            int LookupLimit,
            bool TrackPastKeys
        )
        {
            public TrieStore CreateTrieStore()
            {
                IPruningStrategy pruneStrategy = dirtyNodeSize == -1
                    ? No.Pruning
                    : Prune.WhenCacheReaches(dirtyNodeSize);

                return TestTrieStoreFactory.Build(
                    new MemDb(),
                    pruneStrategy,
                    Persist.EveryNBlock(PersistEveryN),
                    new PruningConfig()
                    {
                        TrackPastKeys = TrackPastKeys,
                        PruningBoundary = LookupLimit,
                    },
                    LimboLogs.Instance);
            }
            public override string ToString()
            {
                return (
                    $"persistEveryN: {PersistEveryN}, " +
                    $"lookup: {LookupLimit}");
            }

            public bool IsMissingAccountExpected(int depth)
            {
                if (TrackPastKeys)
                {
                    return depth > LookupLimit;
                }
                else
                {
                    return depth % PersistEveryN != 0;
                }
            }
        }

        private static IEnumerable<TrieStoreConfigurations> CreateTrieStoreConfigurations()
        {
            yield return new TrieStoreConfigurations(1.MiB(), 8, 8, false);
            yield return new TrieStoreConfigurations(1.MiB(), 8, 8, true);
            yield return new TrieStoreConfigurations(-1, 1, 8, false);
        }

        private static IEnumerable<(TrieStoreConfigurations, int, int, int)> FuzzAccountScenarios()
        {
            foreach (var trieStoreConfigurations in CreateTrieStoreConfigurations())
            {
                yield return new(trieStoreConfigurations, 128, 128, 8);
            }
        }

        [TestCaseSource(nameof(FuzzAccountScenarios))]
        [Repeat(10)]
        public void Fuzz_accounts(
            (TrieStoreConfigurations trieStoreConfig,
            int accountsCount,
            int blocksCount,
            int uniqueValuesCount) test)
        {
            (TrieStoreConfigurations trieStoreConfig, int accountsCount, int blocksCount, int uniqueValuesCount) = test;

            string fileName = Path.GetTempFileName();
            _logger.Info(
                $"Fuzzing with accounts: {accountsCount}, " +
                $"blocks {blocksCount}, " +
                $"values: {uniqueValuesCount}, " +
                $"{trieStoreConfig} into file {fileName}");

            using FileStream fileStream = new(fileName, FileMode.Create);
            using StreamWriter streamWriter = new(fileStream);

            Queue<Hash256> rootQueue = new();

            using TrieStore trieStore = trieStoreConfig.CreateTrieStore();
            StateTree patriciaTree = new(trieStore, _logManager);

            byte[][] accounts = new byte[accountsCount][];
            byte[][] randomValues = new byte[uniqueValuesCount][];

            for (int i = 0; i < randomValues.Length; i++)
            {
                bool isEmptyValue = _random.Next(0, 2) == 0;
                if (isEmptyValue)
                {
                    randomValues[i] = [];
                }
                else
                {
                    randomValues[i] = TestItem.GenerateRandomAccountRlp();
                }
            }

            for (int accountIndex = 0; accountIndex < accounts.Length; accountIndex++)
            {
                byte[] key = new byte[32];
                ((UInt256)accountIndex).ToBigEndian(key);
                accounts[accountIndex] = key;
            }

            for (int blockNumber = 0; blockNumber < blocksCount; blockNumber++)
            {
                bool isEmptyBlock = _random.Next(5) == 0;
                if (!isEmptyBlock)
                {
                    for (int i = 0; i < Math.Max(1, accountsCount / 8); i++)
                    {
                        int randomAccountIndex = _random.Next(accounts.Length);
                        int randomValueIndex = _random.Next(randomValues.Length);

                        byte[] account = accounts[randomAccountIndex];
                        byte[] value = randomValues[randomValueIndex];

                        streamWriter.WriteLine(
                            $"Block {blockNumber} - setting {account.ToHexString()} = {value.ToHexString()}");
                        patriciaTree.Set(account, value);
                    }
                }

                streamWriter.WriteLine(
                    $"Commit block {blockNumber} | empty: {isEmptyBlock}");
                patriciaTree.UpdateRootHash();
                trieStore.CommitPatriciaTrie(blockNumber, patriciaTree);
                rootQueue.Enqueue(patriciaTree.RootHash);
            }

            streamWriter.Flush();
            fileStream.Seek(0, SeekOrigin.Begin);

            int verifiedBlocks = 0;

            while (rootQueue.TryDequeue(out Hash256 currentRoot))
            {
                try
                {
                    patriciaTree.RootHash = currentRoot;
                    for (int i = 0; i < accounts.Length; i++)
                    {
                        patriciaTree.Get(accounts[i]);
                    }

                    _logger.Info($"Verified positive {verifiedBlocks}");
                }
                catch (MissingTrieNodeException)
                {
                    if (!trieStoreConfig.IsMissingAccountExpected(blocksCount - verifiedBlocks))
                        throw;

                    _logger.Info($"Verified negative {verifiedBlocks}");
                }

                verifiedBlocks++;
            }
        }

        private static IEnumerable<(TrieStoreConfigurations, int accountsCount, int blocksCount, int uniqueValuesCount, int? seed)> FuzzAccountsWithReorganizationsScenarios()
        {
            foreach (var trieStoreConfiguration in CreateTrieStoreConfigurations())
            {
                yield return (trieStoreConfiguration, 4, 16, 4, null);
            }
        }

        [TestCaseSource(nameof(FuzzAccountsWithReorganizationsScenarios))]
        public void Fuzz_accounts_with_reorganizations(
            (TrieStoreConfigurations trieStoreConfig,
            int accountsCount,
            int blocksCount,
            int uniqueValuesCount,
            int? seed) scenario)
        {
            (TrieStoreConfigurations trieStoreConfig,
                int accountsCount,
                int blocksCount,
                int uniqueValuesCount,
                int? seed) = scenario;

            int usedSeed = seed ?? _random.Next(int.MaxValue);
            _random = new Random(usedSeed);

            _logger.Info($"RANDOM SEED {usedSeed}");
            string fileName = Path.GetTempFileName();
            //string fileName = "C:\\Temp\\fuzz.txt";
            _logger.Info(
                $"Fuzzing with accounts: {accountsCount}, " +
                $"blocks {blocksCount}, " +
                $"values: {uniqueValuesCount}, " +
                $"{trieStoreConfig} into file {fileName}");

            using FileStream fileStream = new(fileName, FileMode.Create);
            using StreamWriter streamWriter = new(fileStream);

            Queue<Hash256> rootQueue = new();
            Stack<Hash256> rootStack = new();

            using TrieStore trieStore = trieStoreConfig.CreateTrieStore();
            PatriciaTree patriciaTree = new(trieStore, _logManager);

            byte[][] accounts = new byte[accountsCount][];
            byte[][] randomValues = new byte[uniqueValuesCount][];

            for (int i = 0; i < randomValues.Length; i++)
            {
                bool isEmptyValue = _random.Next(0, 2) == 0;
                if (isEmptyValue)
                {
                    randomValues[i] = [];
                }
                else
                {
                    randomValues[i] = TestItem.GenerateRandomAccountRlp();
                }
            }

            for (int accountIndex = 0; accountIndex < accounts.Length; accountIndex++)
            {
                byte[] key = new byte[32];
                ((UInt256)accountIndex).ToBigEndian(key);
                accounts[accountIndex] = key;
            }

            int blockCount = 0;
            for (int blockNumber = 0; blockNumber < blocksCount; blockNumber++)
            {
                int reorgDepth = _random.Next(Math.Min(5, blockCount));
                _logger.Debug($"Reorganizing {reorgDepth}");

                for (int i = 0; i < reorgDepth; i++)
                {
                    try
                    {
                        // no longer need undo?
                        // trieStore.UndoOneBlock();
                    }
                    catch (InvalidOperationException)
                    {
                        // if memory limit hits in
                        blockCount = 0;
                    }

                    rootStack.Pop();
                    patriciaTree.RootHash = rootStack.Peek();
                }

                blockCount = Math.Max(0, blockCount - reorgDepth);
                _logger.Debug($"Setting block count to {blockCount}");

                bool isEmptyBlock = _random.Next(5) == 0;
                if (!isEmptyBlock)
                {
                    for (int i = 0; i < Math.Max(1, accountsCount / 8); i++)
                    {
                        int randomAccountIndex = _random.Next(accounts.Length);
                        int randomValueIndex = _random.Next(randomValues.Length);

                        byte[] account = accounts[randomAccountIndex];
                        byte[] value = randomValues[randomValueIndex];

                        streamWriter.WriteLine(
                            $"Block {blockCount} - setting {account.ToHexString()} = {value.ToHexString()}");
                        patriciaTree.Set(account, value);
                    }
                }

                streamWriter.WriteLine(
                    $"Commit block {blockCount} | empty: {isEmptyBlock}");
                patriciaTree.UpdateRootHash();
                trieStore.CommitPatriciaTrie(blockNumber, patriciaTree);
                rootQueue.Enqueue(patriciaTree.RootHash);
                rootStack.Push(patriciaTree.RootHash);
                blockCount++;
                _logger.Debug($"Setting block count to {blockCount}");
            }

            streamWriter.Flush();
            fileStream.Seek(0, SeekOrigin.Begin);

            int verifiedBlocks = 0;

            rootQueue.Clear();
            Stack<Hash256> stackCopy = new();
            while (rootStack.Count != 0)
            {
                stackCopy.Push(rootStack.Pop());
            }

            rootStack = stackCopy;

            while (rootStack.TryPop(out Hash256 currentRoot))
            {
                try
                {
                    patriciaTree.RootHash = currentRoot;
                    for (int i = 0; i < accounts.Length; i++)
                    {
                        patriciaTree.Get(accounts[i]);
                    }

                    _logger.Info($"Verified positive {verifiedBlocks}");
                }
                catch (MissingTrieNodeException)
                {
                    if (!trieStoreConfig.IsMissingAccountExpected(blocksCount - verifiedBlocks))
                    {
                        throw;
                    }

                    _logger.Info($"Verified negative {verifiedBlocks} (which is ok on block {verifiedBlocks})");
                }

                verifiedBlocks++;
            }
        }

        private static IEnumerable<(TrieStoreConfigurations, int accountsCount, int blocksCount, int? seed)> FuzzAccountsWithStorageScenarios()
        {
            foreach (var trieStoreConfiguration in CreateTrieStoreConfigurations())
            {
                yield return (trieStoreConfiguration, 96, 192, 1541344441);
                yield return (trieStoreConfiguration, 128, 2568, 988091870);
                yield return (trieStoreConfiguration, 128, 2568, 2107374965);
                yield return (trieStoreConfiguration, 128, 2568, null);
                yield return (trieStoreConfiguration, 4, 16, 1242692908);
                yield return (trieStoreConfiguration, 8, 32, 1543322391);
            }
        }

        [TestCaseSource(nameof(FuzzAccountsWithStorageScenarios))]
        public void Fuzz_accounts_with_storage(
            (TrieStoreConfigurations trieStoreConfigurations,
            int accountsCount,
            int blocksCount,
            int? seed) scenario)
        {
            (TrieStoreConfigurations trieStoreConfigurations, int accountsCount, int blocksCount, int? seed) = scenario;

            int usedSeed = seed ?? _random.Next(int.MaxValue);
            _random = new Random(usedSeed);
            _logger.Info($"RANDOM SEED {usedSeed}");

            string fileName = Path.GetTempFileName();
            //string fileName = "C:\\Temp\\fuzz.txt";
            _logger.Info(
                $"Fuzzing with accounts: {accountsCount}, " +
                $"blocks {blocksCount}, " +
                $"{trieStoreConfigurations} into file {fileName}");

            using FileStream fileStream = new(fileName, FileMode.Create);
            using StreamWriter streamWriter = new(fileStream);

            Queue<Hash256> rootQueue = new();

            using TrieStore trieStore = trieStoreConfigurations.CreateTrieStore();
            WorldState stateProvider = new WorldState(trieStore, new MemDb(), _logManager);

            Account[] accounts = new Account[accountsCount];
            Address[] addresses = new Address[accountsCount];

            for (int i = 0; i < accounts.Length; i++)
            {
                bool isEmptyValue = _random.Next(0, 2) == 0;
                if (isEmptyValue)
                {
                    accounts[i] = Account.TotallyEmpty;
                }
                else
                {
                    accounts[i] = TestItem.GenerateRandomAccount();
                }

                addresses[i] = TestItem.GetRandomAddress(_random);
            }

            for (int blockNumber = 0; blockNumber < blocksCount; blockNumber++)
            {
                bool isEmptyBlock = _random.Next(5) == 0;
                if (!isEmptyBlock)
                {
                    for (int i = 0; i < Math.Max(1, accountsCount / 8); i++)
                    {
                        int randomAddressIndex = _random.Next(addresses.Length);
                        int randomAccountIndex = _random.Next(accounts.Length);

                        Address address = addresses[randomAddressIndex];
                        Account account = accounts[randomAccountIndex];

                        if (stateProvider.AccountExists(address))
                        {
                            Account existing = stateProvider.GetAccount(address);
                            if (existing.Balance != account.Balance)
                            {
                                if (account.Balance > existing.Balance)
                                {
                                    stateProvider.AddToBalance(
                                        address, account.Balance - existing.Balance, MuirGlacier.Instance);
                                }
                                else
                                {
                                    stateProvider.SubtractFromBalance(
                                        address, existing.Balance - account.Balance, MuirGlacier.Instance);
                                }

                                stateProvider.IncrementNonce(address, UInt256.One);
                            }

                            byte[] storage = new byte[1];
                            _random.NextBytes(storage);
                            stateProvider.Set(new StorageCell(address, 1), storage);
                        }
                        else if (!account.IsTotallyEmpty)
                        {
                            stateProvider.CreateAccount(address, account.Balance);

                            byte[] storage = new byte[1];
                            _random.NextBytes(storage);
                            stateProvider.Set(new StorageCell(address, 1), storage);
                        }
                    }
                }

                streamWriter.WriteLine(
                    $"Commit block {blockNumber} | empty: {isEmptyBlock}");

                stateProvider.Commit(MuirGlacier.Instance);

                stateProvider.CommitTree(blockNumber);

                if (blockNumber > blocksCount - Reorganization.MaxDepth)
                {
                    rootQueue.Enqueue(stateProvider.StateRoot);
                }
            }

            streamWriter.Flush();
            fileStream.Seek(0, SeekOrigin.Begin);

            int verifiedBlocks = 0;

            while (rootQueue.TryDequeue(out Hash256 currentRoot))
            {
                try
                {
                    stateProvider.StateRoot = currentRoot;
                    for (int i = 0; i < addresses.Length; i++)
                    {
                        if (stateProvider.AccountExists(addresses[i]))
                        {
                            for (int j = 0; j < 256; j++)
                            {
                                stateProvider.Get(new StorageCell(addresses[i], (UInt256)j));
                            }
                        }
                    }

                    _logger.Info($"Verified positive {verifiedBlocks}");
                }
                catch (MissingTrieNodeException)
                {
                    if (!trieStoreConfigurations.IsMissingAccountExpected(blocksCount - verifiedBlocks))
                    {
                        throw;
                    }

                    _logger.Info($"Verified negative {verifiedBlocks} which is ok here");
                }

                verifiedBlocks++;
            }
        }
    }
}
