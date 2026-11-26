// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Threading;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie.Pruning;
using NUnit.Framework;
using NSubstitute;

namespace Nethermind.Trie.Test
{
    [TestFixture]
    [Parallelizable(ParallelScope.All)]
    [FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
    public class TrieTests
    {
        [Test]
        public void Oversized_storage_leaf_is_rejected()
        {
            StorageTree tree = new(NullTrieStore.Instance, LimboLogs.Instance);
            byte[] oversized = new byte[33];
            Array.Fill(oversized, (byte)1);
            tree.Set(UInt256.One, oversized);
            Assert.Throws<TrieException>(() => tree.Get(UInt256.One, out _));
        }

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

        private ITrieStore CreateTrieStore(IDb? memDb = null) => new TestRawTrieStore(memDb ?? new MemDb());

        [Test]
        public void Single_leaf()
        {
            MemDb memDb = new();
            using ITrieStore trieStore = CreateTrieStore(memDb);
            PatriciaTree patriciaTree = new(trieStore, _logManager);
            patriciaTree.Set(_keyA, _longLeaf1);
            patriciaTree.Commit();

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
            patriciaTree.Commit();

            // leaf (root)

            PatriciaTree checkTree = CreateCheckTree(trieStore, patriciaTree);
            Assert.That(checkTree.Get(_keyA).ToArray(), Is.Not.EqualTo(_longLeaf1));
            Assert.That(checkTree.Get(_keyA), Is.SequenceEqualTo(_longLeaf2));
        }

        [Test]
        public void Single_leaf_update_next_blocks()
        {
            MemDb memDb = new();
            using ITrieStore trieStore = CreateTrieStore(memDb);
            PatriciaTree patriciaTree = new(trieStore, _logManager);
            patriciaTree.Set(_keyA, _longLeaf1);
            patriciaTree.Commit();
            patriciaTree.Set(_keyA, _longLeaf2);
            patriciaTree.Commit();
            patriciaTree.UpdateRootHash();

            // leaf (root)

            PatriciaTree checkTree = CreateCheckTree(trieStore, patriciaTree);
            Assert.That(checkTree.Get(_keyA).ToArray(), Is.Not.EqualTo(_longLeaf1));
            Assert.That(checkTree.Get(_keyA), Is.SequenceEqualTo(_longLeaf2));
        }

        [Test]
        public void Single_leaf_delete_same_block()
        {
            MemDb memDb = new();
            using ITrieStore trieStore = CreateTrieStore(memDb);
            PatriciaTree patriciaTree = new(trieStore, _logManager);
            patriciaTree.Set(_keyA, _longLeaf1);
            patriciaTree.Set(_keyA, []);
            patriciaTree.Commit();

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
            patriciaTree.Commit();
            patriciaTree.Set(_keyA, []);
            patriciaTree.Commit();
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
            patriciaTree.Commit();
            patriciaTree.Commit();
            patriciaTree.Commit();
            patriciaTree.Set(_keyA, _longLeaf1);
            patriciaTree.Commit();
            patriciaTree.Commit();
            patriciaTree.Set(_keyA, []);
            patriciaTree.Commit();
            patriciaTree.Set(_keyB, _longLeaf2);
            patriciaTree.Commit();
            patriciaTree.Commit();
            patriciaTree.Commit();
            patriciaTree.Commit();
            patriciaTree.Commit();
            patriciaTree.Commit();
            patriciaTree.Set(_keyB, []);
            patriciaTree.Commit();
            patriciaTree.Commit();
            patriciaTree.UpdateRootHash();

            // leaf (root)

            PatriciaTree checkTree = CreateCheckTree(trieStore, patriciaTree);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(checkTree.Get(_keyA).ToArray(), Is.Empty);
                Assert.That(checkTree.Get(_keyB).ToArray(), Is.Empty);
            }
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
            patriciaTree.Commit();

            // leaf (root)
            PatriciaTree checkTree = CreateCheckTree(trieStore, patriciaTree);
            Assert.That(checkTree.Get(_keyA), Is.SequenceEqualTo(_longLeaf1));
            Assert.That(checkTree.Get(_keyB), Is.SequenceEqualTo(_longLeaf1));
            Assert.That(checkTree.Get(_keyC), Is.SequenceEqualTo(_longLeaf1));
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
            patriciaTree.Commit();

            PatriciaTree checkTree = CreateCheckTree(trieStore, patriciaTree);

            byte[] emptyByte = [];
            byte[] emptyByteCompactEncoded = { 0 };

            using (Assert.EnterMultipleScope())
            {
                Assert.That(checkTree.GetNodeByKey(emptyByte, patriciaTree.RootHash), Is.EqualTo(rootNodeHash));
                Assert.That(checkTree.GetNodeByKey(Nibbles.CompactToHexEncode(emptyByteCompactEncoded), patriciaTree.RootHash), Is.EqualTo(rootNodeHash));

                Assert.That(checkTree.GetNodeByKey(branchNodeKey1, patriciaTree.RootHash), Is.EqualTo(branchNodeValue1));
                Assert.That(checkTree.GetNodeByKey([0xff], patriciaTree.RootHash), Is.Empty);
                Assert.That(checkTree.Get(branchNodeKey1).ToArray(), Is.Empty);
            }
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
            patriciaTree.Commit();
            patriciaTree.Set(_keyA, []);
            patriciaTree.Set(_keyB, []);
            patriciaTree.Set(_keyC, []);
            patriciaTree.Commit();
            patriciaTree.UpdateRootHash();

            // leaf (root)
            PatriciaTree checkTree = CreateCheckTree(trieStore, patriciaTree);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(checkTree.Get(_keyA).ToArray(), Is.Empty);
                Assert.That(checkTree.Get(_keyB).ToArray(), Is.Empty);
                Assert.That(checkTree.Get(_keyC).ToArray(), Is.Empty);
            }
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

            patriciaTree.Commit();

            patriciaTree.UpdateRootHash();

            PatriciaTree checkTree = CreateCheckTree(trieStore, patriciaTree);
            for (int j = 0; j < i; j++)
            {
                Hash256 key = TestItem.Keccaks[j];
                byte[] value = TestItem.GenerateIndexedAccountRlp(j);
                Assert.That(checkTree.Get(key.Bytes), Is.SequenceEqualTo(value), $@"{i} {j}");
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

            patriciaTree.Commit();
            patriciaTree.UpdateRootHash();

            PatriciaTree checkTree = CreateCheckTree(trieStore, patriciaTree);

            // confirm nothing deleted
            for (int j = 0; j < i; j++)
            {
                Hash256 key = TestItem.Keccaks[j];
                byte[] value = TestItem.GenerateIndexedAccountRlp(j);
                Assert.That(checkTree.Get(key.Bytes), Is.SequenceEqualTo(value), $@"{i} {j}");
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

            patriciaTree.Commit();
            patriciaTree.UpdateRootHash();

            PatriciaTree checkTree = CreateCheckTree(trieStore, patriciaTree);
            for (int j = 0; j < i; j++)
            {
                Hash256 key = TestItem.Keccaks[j];
                byte[] value = TestItem.GenerateIndexedAccountRlp(j + 1);
                Assert.That(checkTree.Get(key.Bytes), Is.SequenceEqualTo(value), $@"{i} {j}");
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

            patriciaTree.Commit();

            for (int j = 0; j < i; j++)
            {
                Hash256 key = TestItem.Keccaks[j];
                byte[] value = TestItem.GenerateIndexedAccountRlp(j + 1);
                patriciaTree.Set(key.Bytes, value);
                _logger.Trace($"Setting {key.Bytes.ToHexString()} = {value.ToHexString()}");
            }

            patriciaTree.Commit();
            patriciaTree.UpdateRootHash();

            PatriciaTree checkTree = CreateCheckTree(trieStore, patriciaTree);
            for (int j = 0; j < i; j++)
            {
                Hash256 key = TestItem.Keccaks[j];
                byte[] value = TestItem.GenerateIndexedAccountRlp(j + 1);

                _logger.Trace($"Checking {key.Bytes.ToHexString()} = {value.ToHexString()}");
                Assert.That(checkTree.Get(key.Bytes), Is.SequenceEqualTo(value), $@"{i} {j}");
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

            patriciaTree.Commit();
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

            patriciaTree.Commit();

            for (int j = 0; j < i; j++)
            {
                Hash256 key = TestItem.Keccaks[j];
                patriciaTree.Set(key.Bytes, []);
            }

            patriciaTree.Commit();
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
            patriciaTree.Commit();

            // leaf (root)
            PatriciaTree checkTree = CreateCheckTree(trieStore, patriciaTree);
            Assert.That(checkTree.Get(_keyA), Is.SequenceEqualTo(_longLeaf1));
            Assert.That(checkTree.Get(_keyB), Is.SequenceEqualTo(_longLeaf1));
            Assert.That(checkTree.Get(_keyC), Is.SequenceEqualTo(_longLeaf1));
            Assert.That(checkTree.Get(_keyD), Is.SequenceEqualTo(_longLeaf1));
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
            patriciaTree.Commit();

            // leaf (root)
            PatriciaTree checkTree = CreateCheckTree(trieStore, patriciaTree);
            Assert.That(checkTree.Get(_keyA).ToArray(), Is.Empty);
            Assert.That(checkTree.Get(_keyB), Is.SequenceEqualTo(_longLeaf1));
            Assert.That(checkTree.Get(_keyC), Is.SequenceEqualTo(_longLeaf1));
            Assert.That(checkTree.Get(_keyD), Is.SequenceEqualTo(_longLeaf1));
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
            patriciaTree.Commit();
            PatriciaTree checkTree = CreateCheckTree(trieStore, patriciaTree);
            Assert.That(checkTree.Get(_keyA), Is.SequenceEqualTo(_longLeaf1));
            Assert.That(checkTree.Get(_keyB), Is.SequenceEqualTo(_longLeaf2));
        }

        [Test]
        public void Extension_with_branch_with_two_same_children()
        {
            MemDb memDb = new();
            using ITrieStore trieStore = CreateTrieStore(memDb);
            PatriciaTree patriciaTree = new(trieStore, _logManager);
            patriciaTree.Set(_keyA, _longLeaf1);
            patriciaTree.Set(_keyB, _longLeaf1);
            patriciaTree.Commit();
            PatriciaTree checkTree = CreateCheckTree(trieStore, patriciaTree);
            Assert.That(checkTree.Get(_keyA), Is.SequenceEqualTo(_longLeaf1));
            Assert.That(checkTree.Get(_keyB), Is.SequenceEqualTo(_longLeaf1));
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
            patriciaTree.Commit();
            patriciaTree.Set(_keyA, _longLeaf3);
            patriciaTree.Set(_keyA, _longLeaf1);
            patriciaTree.UpdateRootHash();
            patriciaTree.Commit();

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
            patriciaTree.Commit();
            patriciaTree.Set(_keyA, _longLeaf3);
            patriciaTree.Set(_keyA, _longLeaf1);
            patriciaTree.UpdateRootHash();
            patriciaTree.Commit();

            PatriciaTree checkTree = CreateCheckTree(trieStore, patriciaTree);
            Assert.That(checkTree.Get(_keyA), Is.SequenceEqualTo(_longLeaf1));
            Assert.That(checkTree.Get(_keyB), Is.SequenceEqualTo(_longLeaf1));
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
            patriciaTree.Commit();

            PatriciaTree checkTree = CreateCheckTree(trieStore, patriciaTree);
            Assert.That(checkTree.Get(key1), Is.SequenceEqualTo(_longLeaf1));
            Assert.That(checkTree.Get(key2), Is.SequenceEqualTo(_longLeaf1));
            Assert.That(checkTree.Get(key3), Is.SequenceEqualTo(_longLeaf1));
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
            patriciaTree.Commit();
            patriciaTree.Set(key3, []);
            patriciaTree.UpdateRootHash();
            patriciaTree.Commit();

            PatriciaTree checkTree = CreateCheckTree(trieStore, patriciaTree);
            Assert.That(checkTree.Get(key1), Is.SequenceEqualTo(_longLeaf1));
            Assert.That(checkTree.Get(key2), Is.SequenceEqualTo(_longLeaf1));
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
            patriciaTree.Commit();
            patriciaTree.Set(_keyA, _longLeaf3);
            patriciaTree.Set(_keyA, _longLeaf1);
            patriciaTree.UpdateRootHash();
            patriciaTree.Commit();

            PatriciaTree checkTree = CreateCheckTree(trieStore, patriciaTree);
            Assert.That(checkTree.Get(_keyA), Is.SequenceEqualTo(_longLeaf1));
            Assert.That(checkTree.Get(_keyB), Is.SequenceEqualTo(_longLeaf1));
            Assert.That(checkTree.Get(_keyC), Is.SequenceEqualTo(_longLeaf1));
            Assert.That(checkTree.Get(_keyD), Is.SequenceEqualTo(_longLeaf1));
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

            tree.Commit();

            Parallel.For(0, repetition, (index, _) =>
            {
                foreach ((Hash256, Hash256) it in kv)
                {
                    (Hash256 key, Hash256 value) = it;
                    Assert.That(tree.Get(key.Bytes), Is.SequenceEqualTo(value.BytesToArray()));
                }
            });
        }

        [Test]
        public void Commit_matches_serial_nodes_and_root(
            [Values(4, 16, 128, 1200)] int count,
            [Values(0, 4)] int sharedPrefix,
            [Values] bool skipRoot)
        {
            CommitRecorder serial = new(parallel: false);
            CommitRecorder parallel = new(parallel: true);
            PatriciaTree expected = CreateCommitTree(serial, count, sharedPrefix);
            PatriciaTree actual = CreateCommitTree(parallel, count, sharedPrefix);
            TrieNode root = actual.RootRef!;
            expected.Commit(skipRoot);
            actual.Commit(skipRoot);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(actual.RootHash, Is.EqualTo(expected.RootHash));
                Assert.That(parallel.Nodes.Count, Is.EqualTo(serial.Nodes.Count));
                foreach ((TreePath path, byte[] rlp) in serial.Nodes)
                    Assert.That(parallel.Nodes.GetValueOrDefault(path), Is.EqualTo(rlp), $"Node at {path}");
                Assert.That(parallel.DuplicateWrites, Is.Zero);
                Assert.That(parallel.ActiveAtDispose, Is.Zero);
                Assert.That(parallel.Disposed, Is.True);
                Assert.That(serial.Threads.Count, Is.EqualTo(1));
            }

            // The tree must keep the committer's sealed replacements, not the nodes it handed over.
            // Commit reloads RootRef from the store, so walk the children of the root held before it.
            int reachable = 0;
            AssertKeepsReplacements(root, TreePath.Empty);
            Assert.That(reachable, Is.EqualTo(parallel.Replacements.Count - (skipRoot ? 0 : 1)));

            void AssertKeepsReplacements(TrieNode node, TreePath path)
            {
                if (path.Length > 0)
                {
                    if (parallel.Replacements.ContainsKey(node)) reachable++;
                    Assert.That(parallel.Originals.ContainsKey(node), Is.False, $"Committed original kept at {path}");
                }

                int children = node.IsBranch ? 16 : node.NodeType == NodeType.Extension ? 1 : 0;
                for (int i = 0; i < children; i++)
                {
                    TreePath childPath = path;
                    node.AppendChildPath(ref childPath, i);
                    if (node.GetChildWithChildPath(actual.TrieStore, ref childPath, i, keepChildRef: true) is { } child)
                        AssertKeepsReplacements(child, childPath);
                }
            }
        }

        [Test]
        public void Commit_obeys_shared_worker_limit([Values(1, 2)] int concurrency, [Values] bool fail)
        {
            CommitRecorder recorder = new(parallel: true, fail);
            PatriciaTree tree = CreateCommitTree(recorder, 1200, 0);
            using ParallelUnbalancedWork.WorkerScope scope = ParallelUnbalancedWork.BeginWorkerScope(concurrency);
            if (fail)
                Assert.Throws<InvalidOperationException>(() => tree.Commit());
            else
                tree.Commit();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(recorder.MaxActive, Is.InRange(1, concurrency));
                if (concurrency == 1) Assert.That(recorder.Threads.Count, Is.EqualTo(1));
                Assert.That(recorder.ActiveAtDispose, Is.Zero);
                Assert.That(recorder.Disposed, Is.True);
            }
        }

        private static PatriciaTree CreateCommitTree(CommitRecorder recorder, int count, int sharedPrefix)
        {
            IScopedTrieStore store = Substitute.For<IScopedTrieStore>();
            store.BeginCommit(Arg.Any<TrieNode>(), Arg.Any<WriteFlags>()).Returns(recorder);
            store.FindCachedOrUnknown(Arg.Any<TreePath>(), Arg.Any<Hash256>())
                .Returns(call => new TrieNode(NodeType.Unknown, call.Arg<Hash256>()));
            PatriciaTree tree = new(store, LimboLogs.Instance);
            Random random = new(42);
            for (int i = 0; i < count; i++)
            {
                byte[] key = new byte[32];
                random.NextBytes(key);
                key.AsSpan(0, sharedPrefix).Clear();
                tree.Set(key, key);
            }
            return tree;
        }

        private sealed class CommitRecorder(bool parallel, bool fail = false) : ICommitter
        {
            public readonly ConcurrentDictionary<TreePath, byte[]> Nodes = new();
            public readonly ConcurrentDictionary<int, byte> Threads = new();
            public readonly ConcurrentDictionary<TrieNode, byte> Originals = new(ReferenceEqualityComparer.Instance);
            public readonly ConcurrentDictionary<TrieNode, byte> Replacements = new(ReferenceEqualityComparer.Instance);
            public int DuplicateWrites;
            public int ActiveAtDispose;
            public int MaxActive;
            public bool Disposed;
            private int _active;

            public bool TryEnableParallelCommit() => parallel;

            public TrieNode CommitNode(ref TreePath path, TrieNode node)
            {
                int active = Interlocked.Increment(ref _active);
                int previous;
                do
                {
                    previous = Volatile.Read(ref MaxActive);
                } while (active > previous && Interlocked.CompareExchange(ref MaxActive, active, previous) != previous);
                try
                {
                    Threads.TryAdd(Environment.CurrentManagedThreadId, 0);
                    if (fail) throw new InvalidOperationException("Commit failed");
                    if (!Nodes.TryAdd(path, node.FullRlp.AsSpan().ToArray())) Interlocked.Increment(ref DuplicateWrites);
                    if (!parallel) return node;
                    TrieNode replacement = node.Clone();
                    replacement.Keccak = node.Keccak;
                    replacement.Seal();
                    Originals.TryAdd(node, 0);
                    Replacements.TryAdd(replacement, 0);
                    return replacement;
                }
                finally
                {
                    Interlocked.Decrement(ref _active);
                }
            }

            public void Dispose()
            {
                ActiveAtDispose = Volatile.Read(ref _active);
                Disposed = true;
            }
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

                tree.Commit();
            }, CancellationToken.None, TaskCreationOptions.None, schedulerPair.ConcurrentScheduler);

            Assert.That(task.Wait(TimeSpan.FromSeconds(10)), Is.True, "Commit deadlocked on bounded scheduler");
        }
    }
}
