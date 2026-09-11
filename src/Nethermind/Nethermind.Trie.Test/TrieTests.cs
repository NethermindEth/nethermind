// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Trie.Test
{
    [TestFixture]
    [Parallelizable(ParallelScope.All)]
    [FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
    public class TrieTests
    {
        private ILogger _logger;
        private ILogManager _logManager;
        private Random _random = new();

        [SetUp]
        public void SetUp()
        {
            _logManager = LimboLogs.Instance;
            _logger = _logManager.GetClassLogger<TrieTests>();
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

        private ITrieStore CreateTrieStore(IDb? memDb = null) => TestTrieStoreFactory.Build(memDb ?? new MemDb(), _logManager);

        [Test]
        public void Single_leaf()
        {
            MemDb memDb = new();
            using ITrieStore trieStore = CreateTrieStore(memDb);
            PatriciaTree patriciaTree = new(trieStore, _logManager);
            patriciaTree.Set(_keyA, _longLeaf1);
            trieStore.CommitPatriciaTrie(0, patriciaTree);

            // leaf (root)
        }

        [Test]
        public void Single_leaf_update_same_block()
        {
            MemDb memDb = new();
            using ITrieStore trieStore = CreateTrieStore(memDb);
            PatriciaTree patriciaTree = new(trieStore, _logManager);
            patriciaTree.Set(_keyA, _longLeaf1);
            patriciaTree.Set(_keyA, _longLeaf2);
            trieStore.CommitPatriciaTrie(0, patriciaTree);

            // leaf (root)

            PatriciaTree checkTree = CreateCheckTree(trieStore, patriciaTree);
            Assert.That(checkTree.Get(_keyA).ToArray(), Is.Not.EqualTo(_longLeaf1));
            Assert.That(checkTree.Get(_keyA).ToArray(), Is.EqualTo(_longLeaf2));
        }

        [Test]
        public void Single_leaf_update_next_blocks()
        {
            MemDb memDb = new();
            using ITrieStore trieStore = CreateTrieStore(memDb);
            PatriciaTree patriciaTree = new(trieStore, _logManager);
            patriciaTree.Set(_keyA, _longLeaf1);
            trieStore.CommitPatriciaTrie(0, patriciaTree);
            patriciaTree.Set(_keyA, _longLeaf2);
            trieStore.CommitPatriciaTrie(1, patriciaTree);
            patriciaTree.UpdateRootHash();

            // leaf (root)

            PatriciaTree checkTree = CreateCheckTree(trieStore, patriciaTree);
            Assert.That(checkTree.Get(_keyA).ToArray(), Is.Not.EqualTo(_longLeaf1));
            Assert.That(checkTree.Get(_keyA).ToArray(), Is.EqualTo(_longLeaf2));
        }

        [Test]
        public void Single_leaf_delete_same_block()
        {
            MemDb memDb = new();
            using ITrieStore trieStore = CreateTrieStore(memDb);
            PatriciaTree patriciaTree = new(trieStore, _logManager);
            patriciaTree.Set(_keyA, _longLeaf1);
            patriciaTree.Set(_keyA, []);
            trieStore.CommitPatriciaTrie(0, patriciaTree);

            // leaf (root)

            PatriciaTree checkTree = CreateCheckTree(trieStore, patriciaTree);
            Assert.That(checkTree.Get(_keyA).ToArray(), Is.Empty);
        }

        [Test]
        public void Single_leaf_delete_next_block()
        {
            MemDb memDb = new();
            using ITrieStore trieStore = CreateTrieStore(memDb);
            PatriciaTree patriciaTree = new(trieStore, _logManager);
            patriciaTree.Set(_keyA, _longLeaf1);
            trieStore.CommitPatriciaTrie(0, patriciaTree);
            patriciaTree.Set(_keyA, []);
            trieStore.CommitPatriciaTrie(1, patriciaTree);
            patriciaTree.UpdateRootHash();

            // leaf (root)

            PatriciaTree checkTree = CreateCheckTree(trieStore, patriciaTree);
            Assert.That(checkTree.Get(_keyA).ToArray(), Is.Empty);
        }

        [Test]
        public void Single_leaf_and_keep_for_multiple_dispatches_then_delete()
        {
            MemDb memDb = new();
            using ITrieStore trieStore = CreateTrieStore(memDb);
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

            PatriciaTree checkTree = CreateCheckTree(trieStore, patriciaTree);
            Assert.That(checkTree.Get(_keyA).ToArray(), Is.Empty);
            Assert.That(checkTree.Get(_keyB).ToArray(), Is.Empty);
        }

        [Test]
        public void Branch_with_branch_and_leaf()
        {
            MemDb memDb = new();
            using ITrieStore trieStore = CreateTrieStore(memDb);
            PatriciaTree patriciaTree = new(trieStore, _logManager);
            patriciaTree.Set(_keyA, _longLeaf1);
            patriciaTree.Set(_keyB, _longLeaf1);
            patriciaTree.Set(_keyC, _longLeaf1);
            trieStore.CommitPatriciaTrie(0, patriciaTree);

            // leaf (root)
            PatriciaTree checkTree = CreateCheckTree(trieStore, patriciaTree);
            Assert.That(checkTree.Get(_keyA).ToArray(), Is.EqualTo(_longLeaf1));
            Assert.That(checkTree.Get(_keyB).ToArray(), Is.EqualTo(_longLeaf1));
            Assert.That(checkTree.Get(_keyC).ToArray(), Is.EqualTo(_longLeaf1));
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
            using ITrieStore trieStore = CreateTrieStore(memDb);
            PatriciaTree patriciaTree = new(trieStore, _logManager);
            patriciaTree.Set(_keysA, _longLeaf1);
            patriciaTree.Set(_keysB, _longLeaf1);
            patriciaTree.Set(_keysC, _longLeaf1);
            trieStore.CommitPatriciaTrie(0, patriciaTree);

            PatriciaTree checkTree = CreateCheckTree(trieStore, patriciaTree);

            byte[] emptyByte = [];
            byte[] emptyByteCompactEncoded = { 0 };

            Assert.That(checkTree.GetNodeByKey(emptyByte, patriciaTree.RootHash), Is.EqualTo(rootNodeHash));
            Assert.That(checkTree.GetNodeByKey(Nibbles.CompactToHexEncode(emptyByteCompactEncoded), patriciaTree.RootHash), Is.EqualTo(rootNodeHash));

            Assert.That(checkTree.GetNodeByKey(branchNodeKey1, patriciaTree.RootHash), Is.EqualTo(branchNodeValue1));
            Assert.That(checkTree.GetNodeByKey([0xff], patriciaTree.RootHash), Is.Empty);
            Assert.That(checkTree.Get(branchNodeKey1).ToArray(), Is.Empty);
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
            using ITrieStore trieStore = CreateTrieStore(memDb);
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
            PatriciaTree checkTree = CreateCheckTree(trieStore, patriciaTree);
            Assert.That(checkTree.Get(_keyA).ToArray(), Is.Empty);
            Assert.That(checkTree.Get(_keyB).ToArray(), Is.Empty);
            Assert.That(checkTree.Get(_keyC).ToArray(), Is.Empty);
        }

        public void Test_add_many(int i)
        {
            MemDb memDb = new();
            using ITrieStore trieStore = CreateTrieStore(memDb);
            PatriciaTree patriciaTree = new(trieStore, _logManager);

            for (int j = 0; j < i; j++)
            {
                Hash256 key = TestItem.Keccaks[j];
                byte[] value = TestItem.GenerateIndexedAccountRlp(j);
                patriciaTree.Set(key.Bytes, value);
            }

            trieStore.CommitPatriciaTrie(0, patriciaTree);

            patriciaTree.UpdateRootHash();

            PatriciaTree checkTree = CreateCheckTree(trieStore, patriciaTree);
            for (int j = 0; j < i; j++)
            {
                Hash256 key = TestItem.Keccaks[j];
                byte[] value = TestItem.GenerateIndexedAccountRlp(j);
                Assert.That(checkTree.Get(key.Bytes).ToArray(), Is.EqualTo(value), $@"{i} {j}");
            }
        }

        public void Test_try_delete_and_read_missing_nodes(int i)
        {
            MemDb memDb = new();
            using ITrieStore trieStore = CreateTrieStore(memDb);
            PatriciaTree patriciaTree = new(trieStore, _logManager);

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

            PatriciaTree checkTree = CreateCheckTree(trieStore, patriciaTree);

            // confirm nothing deleted
            for (int j = 0; j < i; j++)
            {
                Hash256 key = TestItem.Keccaks[j];
                byte[] value = TestItem.GenerateIndexedAccountRlp(j);
                Assert.That(checkTree.Get(key.Bytes).ToArray(), Is.EqualTo(value), $@"{i} {j}");
            }

            // read missing
            for (int j = 0; j < i; j++)
            {
                Hash256 key = TestItem.Keccaks[j + 100];
                Assert.That(checkTree.Get(key.Bytes).ToArray(), Is.Empty);
            }
        }

        public void Test_update_many(int i)
        {
            MemDb memDb = new();
            using ITrieStore trieStore = CreateTrieStore(memDb);
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

            PatriciaTree checkTree = CreateCheckTree(trieStore, patriciaTree);
            for (int j = 0; j < i; j++)
            {
                Hash256 key = TestItem.Keccaks[j];
                byte[] value = TestItem.GenerateIndexedAccountRlp(j + 1);
                Assert.That(checkTree.Get(key.Bytes).ToArray(), Is.EqualTo(value), $@"{i} {j}");
            }
        }

        public void Test_update_many_next_block(int i)
        {
            MemDb memDb = new();
            using ITrieStore trieStore = CreateTrieStore(memDb);
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

            PatriciaTree checkTree = CreateCheckTree(trieStore, patriciaTree);
            for (int j = 0; j < i; j++)
            {
                Hash256 key = TestItem.Keccaks[j];
                byte[] value = TestItem.GenerateIndexedAccountRlp(j + 1);

                _logger.Trace($"Checking {key.Bytes.ToHexString()} = {value.ToHexString()}");
                Assert.That(checkTree.Get(key.Bytes).ToArray(), Is.EqualTo(value), $@"{i} {j}");
            }
        }

        public void Test_add_and_delete_many_same_block(int i)
        {
            MemDb memDb = new();
            using ITrieStore trieStore = CreateTrieStore(memDb);
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

            PatriciaTree checkTree = CreateCheckTree(trieStore, patriciaTree);
            for (int j = 0; j < i; j++)
            {
                Hash256 key = TestItem.Keccaks[j];
                Assert.That(checkTree.Get(key.Bytes).ToArray(), Is.Empty, $"{i} {j}");
            }
        }

        public void Test_add_and_delete_many_next_block(int i)
        {
            MemDb memDb = new();
            using ITrieStore trieStore = CreateTrieStore(memDb);
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

            PatriciaTree checkTree = CreateCheckTree(trieStore, patriciaTree);
            for (int j = 0; j < i; j++)
            {
                Hash256 key = TestItem.Keccaks[j];
                Assert.That(checkTree.Get(key.Bytes).ToArray(), Is.Empty, $"{i} {j}");
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
            using ITrieStore trieStore = CreateTrieStore(memDb);
            PatriciaTree patriciaTree = new(trieStore, _logManager);
            patriciaTree.Set(_keyA, _longLeaf1);
            patriciaTree.Set(_keyB, _longLeaf1);
            patriciaTree.Set(_keyC, _longLeaf1);
            patriciaTree.Set(_keyD, _longLeaf1);
            trieStore.CommitPatriciaTrie(0, patriciaTree);

            // leaf (root)
            PatriciaTree checkTree = CreateCheckTree(trieStore, patriciaTree);
            Assert.That(checkTree.Get(_keyA).ToArray(), Is.EqualTo(_longLeaf1));
            Assert.That(checkTree.Get(_keyB).ToArray(), Is.EqualTo(_longLeaf1));
            Assert.That(checkTree.Get(_keyC).ToArray(), Is.EqualTo(_longLeaf1));
            Assert.That(checkTree.Get(_keyD).ToArray(), Is.EqualTo(_longLeaf1));
        }

        [Test]
        public void Two_branches_exactly_same_leaf_then_one_removed()
        {
            MemDb memDb = new();
            using ITrieStore trieStore = CreateTrieStore(memDb);
            PatriciaTree patriciaTree = new(trieStore, _logManager);
            patriciaTree.Set(_keyA, _longLeaf1);
            patriciaTree.Set(_keyB, _longLeaf1);
            patriciaTree.Set(_keyC, _longLeaf1);
            patriciaTree.Set(_keyD, _longLeaf1);
            patriciaTree.Set(_keyA, []);
            trieStore.CommitPatriciaTrie(0, patriciaTree);

            // leaf (root)
            PatriciaTree checkTree = CreateCheckTree(trieStore, patriciaTree);
            Assert.That(checkTree.Get(_keyA).ToArray(), Is.Empty);
            Assert.That(checkTree.Get(_keyB).ToArray(), Is.EqualTo(_longLeaf1));
            Assert.That(checkTree.Get(_keyC).ToArray(), Is.EqualTo(_longLeaf1));
            Assert.That(checkTree.Get(_keyD).ToArray(), Is.EqualTo(_longLeaf1));
        }

        private static PatriciaTree CreateCheckTree(ITrieStore trieStore, PatriciaTree patriciaTree)
        {
            PatriciaTree checkTree = new(trieStore.GetTrieStore(null), LimboLogs.Instance);
            checkTree.RootHash = patriciaTree.RootHash;
            return checkTree;
        }

        [Test]
        public void Extension_with_branch_with_two_different_children()
        {
            MemDb memDb = new();
            using ITrieStore trieStore = CreateTrieStore(memDb);
            PatriciaTree patriciaTree = new(trieStore, _logManager);
            patriciaTree.Set(_keyA, _longLeaf1);
            patriciaTree.Set(_keyB, _longLeaf2);
            trieStore.CommitPatriciaTrie(0, patriciaTree);
            PatriciaTree checkTree = CreateCheckTree(trieStore, patriciaTree);
            Assert.That(checkTree.Get(_keyA).ToArray(), Is.EqualTo(_longLeaf1));
            Assert.That(checkTree.Get(_keyB).ToArray(), Is.EqualTo(_longLeaf2));
        }

        [Test]
        public void Extension_with_branch_with_two_same_children()
        {
            MemDb memDb = new();
            using ITrieStore trieStore = CreateTrieStore(memDb);
            PatriciaTree patriciaTree = new(trieStore, _logManager);
            patriciaTree.Set(_keyA, _longLeaf1);
            patriciaTree.Set(_keyB, _longLeaf1);
            trieStore.CommitPatriciaTrie(0, patriciaTree);
            PatriciaTree checkTree = CreateCheckTree(trieStore, patriciaTree);
            Assert.That(checkTree.Get(_keyA).ToArray(), Is.EqualTo(_longLeaf1));
            Assert.That(checkTree.Get(_keyB).ToArray(), Is.EqualTo(_longLeaf1));
        }

        [Test]
        public void When_branch_with_two_different_children_change_one_and_change_back_next_block()
        {
            MemDb memDb = new();
            using ITrieStore trieStore = CreateTrieStore(memDb);
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
        }

        [Test]
        public void When_branch_with_two_same_children_change_one_and_change_back_next_block()
        {
            MemDb memDb = new();
            using ITrieStore trieStore = CreateTrieStore(memDb);
            PatriciaTree patriciaTree = new(trieStore, _logManager);
            patriciaTree.Set(_keyA, _longLeaf1);
            patriciaTree.Set(_keyB, _longLeaf1);
            patriciaTree.UpdateRootHash();
            trieStore.CommitPatriciaTrie(0, patriciaTree);
            patriciaTree.Set(_keyA, _longLeaf3);
            patriciaTree.Set(_keyA, _longLeaf1);
            patriciaTree.UpdateRootHash();
            trieStore.CommitPatriciaTrie(1, patriciaTree);

            PatriciaTree checkTree = CreateCheckTree(trieStore, patriciaTree);
            Assert.That(checkTree.Get(_keyA).ToArray(), Is.EqualTo(_longLeaf1));
            Assert.That(checkTree.Get(_keyB).ToArray(), Is.EqualTo(_longLeaf1));
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
            using ITrieStore trieStore = CreateTrieStore(memDb);
            PatriciaTree patriciaTree = new(trieStore, _logManager);
            patriciaTree.Set(key1, _longLeaf1);
            patriciaTree.Set(key2, _longLeaf1);
            patriciaTree.Set(key3, _longLeaf1);
            patriciaTree.UpdateRootHash();
            trieStore.CommitPatriciaTrie(0, patriciaTree);

            PatriciaTree checkTree = CreateCheckTree(trieStore, patriciaTree);
            Assert.That(checkTree.Get(key1).ToArray(), Is.EqualTo(_longLeaf1));
            Assert.That(checkTree.Get(key2).ToArray(), Is.EqualTo(_longLeaf1));
            Assert.That(checkTree.Get(key3).ToArray(), Is.EqualTo(_longLeaf1));
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
            using ITrieStore trieStore = CreateTrieStore(memDb);
            PatriciaTree patriciaTree = new(trieStore, _logManager);
            patriciaTree.Set(key1, _longLeaf1);
            patriciaTree.Set(key2, _longLeaf1);
            patriciaTree.Set(key3, _longLeaf1);
            patriciaTree.UpdateRootHash();
            trieStore.CommitPatriciaTrie(0, patriciaTree);
            patriciaTree.Set(key3, []);
            patriciaTree.UpdateRootHash();
            trieStore.CommitPatriciaTrie(1, patriciaTree);

            PatriciaTree checkTree = CreateCheckTree(trieStore, patriciaTree);
            Assert.That(checkTree.Get(key1).ToArray(), Is.EqualTo(_longLeaf1));
            Assert.That(checkTree.Get(key2).ToArray(), Is.EqualTo(_longLeaf1));
            Assert.That(checkTree.Get(key3).ToArray(), Is.Empty);
        }

        [Test]
        public void When_two_branches_with_two_same_children_change_one_and_change_back_next_block()
        {
            MemDb memDb = new();
            using ITrieStore trieStore = CreateTrieStore(memDb);
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

            PatriciaTree checkTree = CreateCheckTree(trieStore, patriciaTree);
            Assert.That(checkTree.Get(_keyA).ToArray(), Is.EqualTo(_longLeaf1));
            Assert.That(checkTree.Get(_keyB).ToArray(), Is.EqualTo(_longLeaf1));
            Assert.That(checkTree.Get(_keyC).ToArray(), Is.EqualTo(_longLeaf1));
            Assert.That(checkTree.Get(_keyD).ToArray(), Is.EqualTo(_longLeaf1));
        }

        [Test]
        public void Can_parallel_read_trees()
        {
            int itemCount = 1024;
            int repetition = 100;

            using ITrieStore trieStore = CreateTrieStore();

            PatriciaTree tree = new(trieStore, LimboLogs.Instance);

            using ArrayPoolList<(Hash256, Hash256)> kv = new(itemCount);

            Span<byte> buffer = stackalloc byte[32];
            for (int i = 0; i < itemCount; i++)
            {
                BinaryPrimitives.WriteInt32BigEndian(buffer, i);
                Hash256 key = Keccak.Compute(buffer);
                key.Bytes[..8].Fill(0);
                kv.Add((key, Keccak.Compute(buffer)));
            }

            foreach ((Hash256, Hash256) it in kv)
            {
                (Hash256 key, Hash256 value) = it;
                tree.Set(key.Bytes, value.BytesToArray());
            }

            using (trieStore.BeginBlockCommit(0))
            {
                tree.Commit();
            }

            Parallel.For(0, repetition, (index, _) =>
            {
                foreach ((Hash256, Hash256) it in kv)
                {
                    (Hash256 key, Hash256 value) = it;
                    Assert.That(tree.Get(key.Bytes).ToArray(), Is.EqualTo(value.BytesToArray()));
                }
            });
        }

        [Test]
        public void WarmUpPath_DoesNotThrow()
        {
            // Build a tree with extension, branch, and leaf nodes: _keyA, _keyB, _keyC, _keyD
            using ITrieStore trieStore = CreateTrieStore();
            PatriciaTree patriciaTree = new(trieStore, _logManager);
            patriciaTree.Set(_keyA, _longLeaf1);
            patriciaTree.Set(_keyB, _longLeaf2);
            patriciaTree.Set(_keyC, _longLeaf1);
            patriciaTree.Set(_keyD, _longLeaf2);
            trieStore.CommitPatriciaTrie(0, patriciaTree);

            // Test warmup on various keys
            Assert.That(() => patriciaTree.WarmUpPath(_keyA), Throws.Nothing);  // Existing key
            Assert.That(() => patriciaTree.WarmUpPath(_keyB), Throws.Nothing);  // Existing key
            Assert.That(() => patriciaTree.WarmUpPath(_keyC), Throws.Nothing);  // Existing key in different branch
            Assert.That(() => patriciaTree.WarmUpPath(_keyD), Throws.Nothing);  // Existing key in different branch
            Assert.That(() => patriciaTree.WarmUpPath(Bytes.FromHexString("00000000000cc")), Throws.Nothing);  // Non-existent key
            Assert.That(() => patriciaTree.WarmUpPath(Bytes.FromHexString("fffffffffffff")), Throws.Nothing);  // Completely different path
        }

        [Test]
        public void WarmUpPath_DoesNotThrow_WhenPersistenceServesAnotherVersionOfTheNode()
        {
            StaleWarmerTrieStore trieStore = new();
            PatriciaTree patriciaTree = new(trieStore, _logManager) { RootHash = StaleWarmerTrieStore.RootHashToWarm };

            Assert.That(() => patriciaTree.WarmUpPath(_keyA), Throws.Nothing);
            Assert.That(patriciaTree.RootRef!.NodeType, Is.EqualTo(NodeType.Unknown));
        }

        [Test]
        public void Commit_DoesNotDeadlock_WhenRunOnBoundedScheduler()
        {
            // Commit should not deadlock on a bounded scheduler (e.g. NewBlock P2P message on BackgroundTaskScheduler).
            ConcurrentExclusiveSchedulerPair schedulerPair = new(TaskScheduler.Default, maxConcurrencyLevel: 1);

            Task task = Task.Factory.StartNew(() =>
            {
                MemDb memDb = new();
                using ITrieStore trieStore = CreateTrieStore(memDb);
                PatriciaTree tree = new(trieStore, _logManager);

                Span<byte> buffer = stackalloc byte[32];
                for (int i = 0; i < 100; i++)
                {
                    BinaryPrimitives.WriteInt32BigEndian(buffer, i);
                    Hash256 key = Keccak.Compute(buffer);
                    tree.Set(key.Bytes, key.BytesToArray());
                }

                using (trieStore.BeginBlockCommit(0))
                {
                    tree.Commit();
                }
            }, CancellationToken.None, TaskCreationOptions.None, schedulerPair.ConcurrentScheduler);

            Assert.That(task.Wait(TimeSpan.FromSeconds(10)), Is.True, "Commit deadlocked on bounded scheduler");
        }

        /// <summary>
        /// A path-keyed store that answers a warmer read with the RLP of another version of the node at that path,
        /// which is what the flat DB does when the warmer runs ahead of, or behind, the live reads.
        /// </summary>
        private class StaleWarmerTrieStore : IScopedTrieStore
        {
            public static readonly Hash256 RootHashToWarm = Keccak.Compute("root to warm");

            private readonly byte[] _rlpOfAnotherNode;

            public StaleWarmerTrieStore()
            {
                TrieNode leaf = TrieNodeFactory.CreateLeaf([0x1, 0x2], new byte[32]);
                TreePath path = TreePath.Empty;
                leaf.ResolveKey(NullTrieNodeResolver.Instance, ref path);
                _rlpOfAnotherNode = leaf.FullRlp.ToArray()!;
            }

            public TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash)
            {
                TrieNode node = new(NodeType.Unknown, hash);
                node.MarkWarmerOwned();
                return node;
            }

            public byte[]? LoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) => _rlpOfAnotherNode;

            public byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) => _rlpOfAnotherNode;

            public ITrieNodeResolver GetStorageTrieNodeResolver(Hash256? address) => this;

            public ICommitter BeginCommit(TrieNode? root, WriteFlags writeFlags = WriteFlags.None) =>
                throw new NotSupportedException();
        }
    }
}
