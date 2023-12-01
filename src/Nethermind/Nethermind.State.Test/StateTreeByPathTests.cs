// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Db.ByPathState;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Store.Test
{
    [TestFixture]
    public class StateTreeByPathTests
    {
        private readonly Account _account0 = Build.An.Account.WithBalance(0).TestObject;
        private readonly Account _account1 = Build.An.Account.WithBalance(1).TestObject;
        private readonly Account _account2 = Build.An.Account.WithBalance(2).TestObject;
        private readonly Account _account3 = Build.An.Account.WithBalance(3).TestObject;

        [SetUp]
        public void Setup()
        {
            Trie.Metrics.TreeNodeHashCalculations = 0;
            Trie.Metrics.TreeNodeRlpDecodings = 0;
            Trie.Metrics.TreeNodeRlpEncodings = 0;
        }

        [Test]
        public void No_reads_when_setting_on_empty()
        {
            MemColumnsDb<StateColumns> stateDb = new();
            MemDb db = (MemDb)stateDb.GetColumnDb(StateColumns.State);
            StateTreeByPath tree = new(new TrieStoreByPath(new ByPathStateDb(stateDb, LimboLogs.Instance), LimboLogs.Instance), LimboLogs.Instance);
            tree.Set(TestItem.AddressA, _account0);
            tree.Set(TestItem.AddressB, _account0);
            tree.Set(TestItem.AddressC, _account0);
            tree.Commit(0);
            Assert.That(db.ReadsCount, Is.EqualTo(0), "reads");
        }

        [Test]
        public void Minimal_writes_when_setting_on_empty()
        {
            MemColumnsDb<StateColumns> stateDb = new();
            MemDb db = (MemDb)stateDb.GetColumnDb(StateColumns.State);
            StateTreeByPath tree = new(new TrieStoreByPath(new ByPathStateDb(stateDb, LimboLogs.Instance), LimboLogs.Instance), LimboLogs.Instance);
            tree.Set(TestItem.AddressA, _account0);
            tree.Set(TestItem.AddressB, _account0);
            tree.Set(TestItem.AddressC, _account0);
            tree.Commit(0);
            Assert.That(db.WritesCount, Is.EqualTo(8), "writes"); // branch, branch, two leaves (one is stored as RLP)
        }

        [Test]
        public void Minimal_writes_when_setting_on_empty_scenario_2()
        {
            MemColumnsDb<StateColumns> stateDb = new();
            MemDb db = (MemDb)stateDb.GetColumnDb(StateColumns.State);
            StateTreeByPath tree = new(new TrieStoreByPath(new ByPathStateDb(stateDb, LimboLogs.Instance), LimboLogs.Instance), LimboLogs.Instance);
            tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), _account0);
            tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb0"), _account0);
            tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb1"), _account0);
            tree.Commit(0);

            tree.Get(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000")).Should().BeEquivalentTo(_account0);
            tree.Get(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb0")).Should().BeEquivalentTo(_account0);
            tree.Get(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb1")).Should().BeEquivalentTo(_account0);
            Assert.That(db.WritesCount, Is.EqualTo(8), "writes"); // extension, branch, leaf, extension, branch, 2x same leaf
            Assert.That(Trie.Metrics.TreeNodeHashCalculations, Is.EqualTo(8), "hashes");
            Assert.That(Trie.Metrics.TreeNodeRlpEncodings, Is.EqualTo(7), "encodings");
        }

        [Test]
        public void Minimal_writes_when_setting_on_empty_scenario_3()
        {
            MemColumnsDb<StateColumns> stateDb = new();
            MemDb db = (MemDb)stateDb.GetColumnDb(StateColumns.State);
            StateTreeByPath tree = new(new TrieStoreByPath(new ByPathStateDb(stateDb, LimboLogs.Instance), LimboLogs.Instance), LimboLogs.Instance);
            tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), _account0);
            tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb0"), _account0);
            tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb1"), _account0);
            tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb1"), null);
            tree.Commit(0);
            Assert.That(db.WritesCount, Is.EqualTo(12), "writes"); // extension, branch, 2x leaf (each node is 2 writes) + deletion writes (2)
            Assert.That(Trie.Metrics.TreeNodeHashCalculations, Is.EqualTo(4), "hashes");
            Assert.That(Trie.Metrics.TreeNodeRlpEncodings, Is.EqualTo(4), "encodings");
        }

        [Test]
        public void Minimal_writes_when_setting_on_empty_scenario_4()
        {
            MemColumnsDb<StateColumns> stateDb = new();
            MemDb db = (MemDb)stateDb.GetColumnDb(StateColumns.State);
            StateTreeByPath tree = new(new TrieStoreByPath(new ByPathStateDb(stateDb, LimboLogs.Instance), LimboLogs.Instance), LimboLogs.Instance);
            tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), _account0);
            tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb0"), _account0);
            tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb1"), _account0);
            tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb0"), null);
            tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb1"), null);
            tree.Commit(0);
            Assert.That(db.WritesCount, Is.EqualTo(14), "writes"); // extension, branch, 2x leaf
            Assert.That(Trie.Metrics.TreeNodeHashCalculations, Is.EqualTo(1), "hashes");
            Assert.That(Trie.Metrics.TreeNodeRlpEncodings, Is.EqualTo(1), "encodings");
        }

        [Test]
        public void Minimal_writes_when_setting_on_empty_scenario_5()
        {
            MemColumnsDb<StateColumns> stateDb = new();
            MemDb db = (MemDb)stateDb.GetColumnDb(StateColumns.State);
            StateTreeByPath tree = new(new TrieStoreByPath(new ByPathStateDb(stateDb, LimboLogs.Instance), LimboLogs.Instance), LimboLogs.Instance);
            tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), _account0);
            tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb0"), _account0);
            tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb1"), _account0);
            tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb0"), null);
            tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb1"), null);
            tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), null);
            tree.Commit(0);
            Assert.That(db.WritesCount, Is.EqualTo(0), "writes"); // extension, branch, 2x leaf
            Assert.That(Trie.Metrics.TreeNodeHashCalculations, Is.EqualTo(0), "hashes");
            Assert.That(Trie.Metrics.TreeNodeRlpEncodings, Is.EqualTo(0), "encodings");
        }

        [Test]
        public void Scenario_traverse_extension_read_full_match()
        {
            MemColumnsDb<StateColumns> stateDb = new();
            MemDb db = (MemDb)stateDb.GetColumnDb(StateColumns.State);
            StateTreeByPath tree = new(new TrieStoreByPath(new ByPathStateDb(stateDb, LimboLogs.Instance), LimboLogs.Instance), LimboLogs.Instance);
            tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), _account0);
            tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111111"), _account1);
            Account account = tree.Get(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111111"));
            //Assert.AreEqual(0, db.ReadsCount);
            Assert.That(account.Balance, Is.EqualTo(_account1.Balance));
            tree.UpdateRootHash();
            Hash256 rootHash = tree.RootHash;
            Assert.That(rootHash.ToString(true), Is.EqualTo("0xf99f1d3234bad8d63d818db36ff63eefc8916263e654db8b800d3bd03f6339a5"));
            tree.Commit(0);
            Assert.That(rootHash.ToString(true), Is.EqualTo("0xf99f1d3234bad8d63d818db36ff63eefc8916263e654db8b800d3bd03f6339a5"));
        }

        [Test]
        public void Scenario_traverse_extension_read_missing()
        {
            MemColumnsDb<StateColumns> stateDb = new();
            StateTreeByPath tree = new(new TrieStoreByPath(new ByPathStateDb(stateDb, LimboLogs.Instance), LimboLogs.Instance), LimboLogs.Instance);
            tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), _account0);
            tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111111"), _account1);
            Account account = tree.Get(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeedddddddddddddddddddddddd"));
            Assert.Null(account);
            tree.UpdateRootHash();
            Hash256 rootHash = tree.RootHash;
            Assert.That(rootHash.ToString(true), Is.EqualTo("0xf99f1d3234bad8d63d818db36ff63eefc8916263e654db8b800d3bd03f6339a5"));
            tree.Commit(0);
            Assert.That(rootHash.ToString(true), Is.EqualTo("0xf99f1d3234bad8d63d818db36ff63eefc8916263e654db8b800d3bd03f6339a5"));
        }

        [Test]
        public void Scenario_traverse_extension_new_branching()
        {
            MemColumnsDb<StateColumns> stateDb = new MemColumnsDb<StateColumns>();
            StateTreeByPath tree = new(new TrieStoreByPath(new ByPathStateDb(stateDb, LimboLogs.Instance), LimboLogs.Instance), LimboLogs.Instance);
            tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), _account0);
            tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111111"), _account1);
            tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeedddddddddddddddddddddddd"), _account2);
            tree.UpdateRootHash();
            Hash256 rootHash = tree.RootHash;
            Assert.That(rootHash.ToString(true), Is.EqualTo("0x543c960143a2a06b685d6b92f0c37000273e616bc23888521e7edf15ad06da46"));
            tree.Commit(0);
            Assert.That(rootHash.ToString(true), Is.EqualTo("0x543c960143a2a06b685d6b92f0c37000273e616bc23888521e7edf15ad06da46"));
        }

        [Test]
        public void Scenario_traverse_extension_delete_missing()
        {
            MemColumnsDb<StateColumns> stateDb = new();
            MemDb db = (MemDb)stateDb.GetColumnDb(StateColumns.State);
            StateTreeByPath tree = new(new TrieStoreByPath(new ByPathStateDb(stateDb, LimboLogs.Instance), LimboLogs.Instance), LimboLogs.Instance);
            tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), _account0);
            tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111111"), _account1);
            tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeddddddddddddddddddddddddd"), null);
            Assert.That(db.ReadsCount, Is.EqualTo(0));
            tree.UpdateRootHash();
            Hash256 rootHash = tree.RootHash;
            Assert.That(rootHash.ToString(true), Is.EqualTo("0xf99f1d3234bad8d63d818db36ff63eefc8916263e654db8b800d3bd03f6339a5"));
            tree.Commit(0);
            Assert.That(rootHash.ToString(true), Is.EqualTo("0xf99f1d3234bad8d63d818db36ff63eefc8916263e654db8b800d3bd03f6339a5"));
        }

        [Test]
        public void Scenario_traverse_extension_create_new_extension()
        {
            MemColumnsDb<StateColumns> stateDb = new();
            MemDb db = (MemDb)stateDb.GetColumnDb(StateColumns.State);
            StateTreeByPath tree = new(new TrieStoreByPath(new ByPathStateDb(stateDb, LimboLogs.Instance), LimboLogs.Instance), LimboLogs.Instance);
            tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), _account0);
            tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111111"), _account1);
            tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeaaaaaaaaaaaaaaaab00000000"), _account2);
            tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeaaaaaaaaaaaaaaaab11111111"), _account3);
            Assert.That(db.ReadsCount, Is.EqualTo(0));
            tree.UpdateRootHash();
            Hash256 rootHash = tree.RootHash;
            Assert.That(rootHash.ToString(true), Is.EqualTo("0x0918112fc898173562441709a2c1cbedb80d1aaecaeadf2f3e9492eeaa568c67"));
            tree.Commit(0);
            Assert.That(rootHash.ToString(true), Is.EqualTo("0x0918112fc898173562441709a2c1cbedb80d1aaecaeadf2f3e9492eeaa568c67"));
        }

        [Test]
        public void Scenario_traverse_leaf_update_new_value()
        {
            MemColumnsDb<StateColumns> stateDb = new();
            StateTreeByPath tree = new(new TrieStoreByPath(new ByPathStateDb(stateDb, LimboLogs.Instance), LimboLogs.Instance), LimboLogs.Instance);
            tree.Set(new Hash256("1111111111111111111111111111111111111111111111111111111111111111"), _account0);
            tree.Set(new Hash256("1111111111111111111111111111111111111111111111111111111111111111"), _account1);
            tree.UpdateRootHash();
            Hash256 rootHash = tree.RootHash;
            Assert.That(rootHash.ToString(true), Is.EqualTo("0xaa5c248d4b4b8c27a654296a8e0cc51131eb9011d9166fa0fca56a966489e169"));
            tree.Commit(0);
            Assert.That(rootHash.ToString(true), Is.EqualTo("0xaa5c248d4b4b8c27a654296a8e0cc51131eb9011d9166fa0fca56a966489e169"));
        }

        [Test]
        public void Scenario_traverse_leaf_update_no_change()
        {
            MemColumnsDb<StateColumns> stateDb = new();
            StateTreeByPath tree = new(new TrieStoreByPath(new ByPathStateDb(stateDb, LimboLogs.Instance), LimboLogs.Instance), LimboLogs.Instance);
            tree.Set(new Hash256("1111111111111111111111111111111111111111111111111111111111111111"), _account0);
            tree.Set(new Hash256("1111111111111111111111111111111111111111111111111111111111111111"), _account0);
            tree.UpdateRootHash();
            Hash256 rootHash = tree.RootHash;
            Assert.That(rootHash.ToString(true), Is.EqualTo("0x491fbb33aaff22c0a7ff68d5c81ec114dddf89d022ccdee838a0e9d6cd45cab4"));
            tree.Commit(0);
            Assert.That(rootHash.ToString(true), Is.EqualTo("0x491fbb33aaff22c0a7ff68d5c81ec114dddf89d022ccdee838a0e9d6cd45cab4"));
        }

        [Test]
        public void Scenario_traverse_leaf_read_matching_leaf()
        {
            MemColumnsDb<StateColumns> stateDb = new();
            StateTreeByPath tree = new(new TrieStoreByPath(new ByPathStateDb(stateDb, LimboLogs.Instance), LimboLogs.Instance), LimboLogs.Instance);
            tree.Set(new Hash256("1111111111111111111111111111111111111111111111111111111111111111"), _account0);
            tree.Set(new Hash256("1111111111111111111111111111111111111111111111111111111111111111"), null);
            tree.UpdateRootHash();
            Hash256 rootHash = tree.RootHash;
            Assert.That(rootHash.ToString(true), Is.EqualTo("0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421"));
            tree.Commit(0);
            Assert.That(rootHash.ToString(true), Is.EqualTo("0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421"));
        }

        [Test]
        public void Scenario_traverse_leaf_delete_missing()
        {
            MemColumnsDb<StateColumns> stateDb = new();
            StateTreeByPath tree = new(new TrieStoreByPath(new ByPathStateDb(stateDb, LimboLogs.Instance), LimboLogs.Instance), LimboLogs.Instance);
            tree.Set(new Hash256("1111111111111111111111111111111111111111111111111111111111111111"), _account0);
            tree.Set(new Hash256("1111111111111111111111111111111ddddddddddddddddddddddddddddddddd"), null);
            tree.UpdateRootHash();
            Hash256 rootHash = tree.RootHash;
            Assert.That(rootHash.ToString(true), Is.EqualTo("0x491fbb33aaff22c0a7ff68d5c81ec114dddf89d022ccdee838a0e9d6cd45cab4"));
            tree.Commit(0);
            Assert.That(rootHash.ToString(true), Is.EqualTo("0x491fbb33aaff22c0a7ff68d5c81ec114dddf89d022ccdee838a0e9d6cd45cab4"));
        }

        [Test]
        public void Scenario_traverse_leaf_update_with_extension()
        {
            MemColumnsDb<StateColumns> stateDb = new();
            StateTreeByPath tree = new(new TrieStoreByPath(new ByPathStateDb(stateDb, LimboLogs.Instance), LimboLogs.Instance), LimboLogs.Instance);
            tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111111111111111111111111111111"), _account0);
            tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000000000000000000000000000"), _account1);
            tree.UpdateRootHash();
            Hash256 rootHash = tree.RootHash;
            Assert.That(rootHash.ToString(true), Is.EqualTo("0x215a4bab4cf2d5ebbaa59c82ae94c9707fcf4cc0ca1fe7e18f918e46db428ef9"));
            tree.Commit(0);
            Assert.That(rootHash.ToString(true), Is.EqualTo("0x215a4bab4cf2d5ebbaa59c82ae94c9707fcf4cc0ca1fe7e18f918e46db428ef9"));
        }

        [Test]
        public void Scenario_traverse_leaf_delete_matching_leaf()
        {
            MemColumnsDb<StateColumns> stateDb = new();
            StateTreeByPath tree = new(new TrieStoreByPath(new ByPathStateDb(stateDb, LimboLogs.Instance), LimboLogs.Instance), LimboLogs.Instance);
            tree.Set(new Hash256("1111111111111111111111111111111111111111111111111111111111111111"), _account0);
            Account account = tree.Get(new Hash256("1111111111111111111111111111111111111111111111111111111111111111"));
            Assert.NotNull(account);
            tree.UpdateRootHash();
            Hash256 rootHash = tree.RootHash;
            Assert.That(rootHash.ToString(true), Is.EqualTo("0x491fbb33aaff22c0a7ff68d5c81ec114dddf89d022ccdee838a0e9d6cd45cab4"));
            tree.Commit(0);
            Assert.That(rootHash.ToString(true), Is.EqualTo("0x491fbb33aaff22c0a7ff68d5c81ec114dddf89d022ccdee838a0e9d6cd45cab4"));
        }

        [Test]
        public void Scenario_traverse_leaf_read_missing()
        {
            MemColumnsDb<StateColumns> stateDb = new();
            StateTreeByPath tree = new(new TrieStoreByPath(new ByPathStateDb(stateDb, LimboLogs.Instance), LimboLogs.Instance), LimboLogs.Instance);
            tree.Set(new Hash256("1111111111111111111111111111111111111111111111111111111111111111"), _account0);
            Account account = tree.Get(new Hash256("111111111111111111111111111111111111111111111111111111111ddddddd"));
            Assert.Null(account);
            tree.UpdateRootHash();
            Hash256 rootHash = tree.RootHash;
            Assert.That(rootHash.ToString(true), Is.EqualTo("0x491fbb33aaff22c0a7ff68d5c81ec114dddf89d022ccdee838a0e9d6cd45cab4"));
            tree.Commit(0);
            Assert.That(rootHash.ToString(true), Is.EqualTo("0x491fbb33aaff22c0a7ff68d5c81ec114dddf89d022ccdee838a0e9d6cd45cab4"));
        }

        [Test]
        public void Scenario_traverse_branch_update_missing()
        {
            MemColumnsDb<StateColumns> stateDb = new();
            StateTreeByPath tree = new(new TrieStoreByPath(new ByPathStateDb(stateDb, LimboLogs.Instance), LimboLogs.Instance), LimboLogs.Instance);
            tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000"), _account0);
            tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111"), _account1);
            tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb22222"), _account2);
            tree.UpdateRootHash();
            Hash256 rootHash = tree.RootHash;
            Assert.That(rootHash.ToString(true), Is.EqualTo("0xc063af0bd3dd88320bc852ff8452049c42fbc06d1a69661567bd427572824cbf"));
            tree.Commit(0);
            Assert.That(rootHash.ToString(true), Is.EqualTo("0xc063af0bd3dd88320bc852ff8452049c42fbc06d1a69661567bd427572824cbf"));
        }

        [Test]
        public void Scenario_traverse_branch_read_missing()
        {
            MemColumnsDb<StateColumns> stateDb = new();
            StateTreeByPath tree = new(new TrieStoreByPath(new ByPathStateDb(stateDb, LimboLogs.Instance), LimboLogs.Instance), LimboLogs.Instance);
            tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000"), _account0);
            tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111"), _account1);
            Account account = tree.Get(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb22222"));
            Assert.Null(account);
            tree.UpdateRootHash();
            Hash256 rootHash = tree.RootHash;
            Assert.That(rootHash.ToString(true), Is.EqualTo("0x94a193704e99c219d9a21428eb37d6d2d71b3d2cea80c77ff0e201c0df70a283"));
            tree.Commit(0);
            Assert.That(rootHash.ToString(true), Is.EqualTo("0x94a193704e99c219d9a21428eb37d6d2d71b3d2cea80c77ff0e201c0df70a283"));
        }

        [Test]
        public void Scenario_traverse_branch_delete_missing()
        {
            MemColumnsDb<StateColumns> stateDb = new();
            StateTreeByPath tree = new(new TrieStoreByPath(new ByPathStateDb(stateDb, LimboLogs.Instance), LimboLogs.Instance), LimboLogs.Instance);
            tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000"), _account0);
            tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111"), _account1);
            tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb22222"), null);
            tree.UpdateRootHash();
            Hash256 rootHash = tree.RootHash;
            Assert.That(rootHash.ToString(true), Is.EqualTo("0x94a193704e99c219d9a21428eb37d6d2d71b3d2cea80c77ff0e201c0df70a283"));
            tree.Commit(0);
            Assert.That(rootHash.ToString(true), Is.EqualTo("0x94a193704e99c219d9a21428eb37d6d2d71b3d2cea80c77ff0e201c0df70a283"));
        }

        [Test]
        public void Minimal_hashes_when_setting_on_empty()
        {
            MemColumnsDb<StateColumns> stateDb = new();
            StateTreeByPath tree = new(new TrieStoreByPath(new ByPathStateDb(stateDb, LimboLogs.Instance), LimboLogs.Instance), LimboLogs.Instance);
            //StateTreeByPath tree = new(new TrieStoreByPath(db, LimboLogs.Instance), LimboLogs.Instance);
            tree.Set(TestItem.AddressA, _account0);
            tree.Set(TestItem.AddressB, _account0);
            tree.Set(TestItem.AddressC, _account0);
            tree.Commit(0);
            tree.Get(TestItem.AddressA);
            tree.Get(TestItem.AddressB);
            tree.Get(TestItem.AddressC);
            Assert.That(Trie.Metrics.TreeNodeHashCalculations, Is.EqualTo(5), "hashes"); // branch, branch, three leaves
        }

        [Test]
        public void Minimal_encodings_when_setting_on_empty()
        {
            MemColumnsDb<StateColumns> stateDb = new();
            StateTreeByPath tree = new(new TrieStoreByPath(new ByPathStateDb(stateDb, LimboLogs.Instance), LimboLogs.Instance), LimboLogs.Instance);
            tree.Set(TestItem.AddressA, _account0);
            tree.Set(TestItem.AddressB, _account0);
            tree.Set(TestItem.AddressC, _account0);
            tree.Commit(0);
            Assert.That(Trie.Metrics.TreeNodeRlpEncodings, Is.EqualTo(5), "encodings"); // branch, branch, three leaves
        }

        [Test]
        public void Zero_decodings_when_setting_on_empty()
        {
            MemColumnsDb<StateColumns> stateDb = new();
            StateTreeByPath tree = new(new TrieStoreByPath(new ByPathStateDb(stateDb, LimboLogs.Instance), LimboLogs.Instance), LimboLogs.Instance);
            tree.Set(TestItem.AddressA, _account0);
            tree.Set(TestItem.AddressB, _account0);
            tree.Set(TestItem.AddressC, _account0);
            tree.Commit(0);
            Assert.That(Trie.Metrics.TreeNodeRlpDecodings, Is.EqualTo(0), "decodings");
        }

        // [Test]
        // public void No_writes_on_continues_update()
        // {
        //     MemDb db = new();
        //     StateTreeByPath tree = new(new TrieStoreByPath(db, LimboLogs.Instance), LimboLogs.Instance);
        //     tree.Set(TestItem.AddressA, _account0);
        //     tree.Set(TestItem.AddressA, _account1);
        //     tree.Set(TestItem.AddressA, _account2);
        //     tree.Set(TestItem.AddressA, _account3);
        //     tree.Commit(0);
        //     Assert.AreEqual(2, db.WritesCount, "writes"); // extension, branch, two leaves
        // }

        [Ignore("This is not critical")]
        [Test]
        public void No_writes_on_reverted_update()
        {
            MemColumnsDb<StateColumns> stateDb = new();
            MemDb stateColumn = stateDb.GetColumnDb(StateColumns.State) as MemDb;
            StateTreeByPath tree = new(new TrieStoreByPath(new ByPathStateDb(stateDb, LimboLogs.Instance), LimboLogs.Instance), LimboLogs.Instance);
            tree.Set(TestItem.AddressA, _account0);
            tree.Commit(0);
            Assert.That(stateColumn.WritesCount, Is.EqualTo(1), "writes before"); // extension, branch, two leaves
            tree.Set(TestItem.AddressA, _account1);
            tree.Set(TestItem.AddressA, _account0);
            tree.Commit(0);
            Assert.That(stateColumn.WritesCount, Is.EqualTo(1), "writes after"); // extension, branch, two leaves
        }

        [Test]
        public void No_writes_without_commit()
        {
            MemColumnsDb<StateColumns> stateDb = new MemColumnsDb<StateColumns>();
            MemDb stateColumn = stateDb.GetColumnDb(StateColumns.State) as MemDb;
            StateTreeByPath tree = new(new TrieStoreByPath(new ByPathStateDb(stateDb, LimboLogs.Instance), LimboLogs.Instance), LimboLogs.Instance);
            tree.Set(TestItem.AddressA, _account0);
            Assert.That(stateColumn.WritesCount, Is.EqualTo(0), "writes");
        }

        [Test]
        public void Can_ask_about_root_hash_without_commiting()
        {
            MemColumnsDb<StateColumns> stateDb = new MemColumnsDb<StateColumns>();
            StateTreeByPath tree = new(new TrieStoreByPath(new ByPathStateDb(stateDb, LimboLogs.Instance), LimboLogs.Instance), LimboLogs.Instance);
            tree.Set(TestItem.AddressA, _account0);
            tree.UpdateRootHash();
            Assert.That(tree.RootHash.ToString(true), Is.EqualTo("0x545a417202afcb10925b2afddb70a698710bb1cf4ab32942c42e9f019d564fdc"));
        }

        [Test]
        public void Can_ask_about_root_hash_without_when_emptied()
        {
            MemColumnsDb<StateColumns> stateDb = new();
            StateTreeByPath tree = new(new TrieStoreByPath(new ByPathStateDb(stateDb, LimboLogs.Instance), LimboLogs.Instance), LimboLogs.Instance);
            tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), _account0);
            tree.UpdateRootHash();
            Assert.That(tree.RootHash, Is.Not.EqualTo(PatriciaTree.EmptyTreeHash));
            tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb0"), _account0);
            tree.UpdateRootHash();
            Assert.That(tree.RootHash, Is.Not.EqualTo(PatriciaTree.EmptyTreeHash));
            tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb1"), _account0);
            tree.UpdateRootHash();
            Assert.That(tree.RootHash, Is.Not.EqualTo(PatriciaTree.EmptyTreeHash));
            tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb0"), null);
            tree.UpdateRootHash();
            Assert.That(tree.RootHash, Is.Not.EqualTo(PatriciaTree.EmptyTreeHash));
            tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb1"), null);
            tree.UpdateRootHash();
            Assert.That(tree.RootHash, Is.Not.EqualTo(PatriciaTree.EmptyTreeHash));
            tree.Set(new Hash256("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), null);
            tree.UpdateRootHash();
            Assert.That(tree.RootHash, Is.EqualTo(PatriciaTree.EmptyTreeHash));
            tree.Commit(0);
            Assert.That(tree.RootHash, Is.EqualTo(PatriciaTree.EmptyTreeHash));
        }

        [Test]
        public void hash_empty_tree_root_hash_initially()
        {
            MemColumnsDb<StateColumns> stateDb = new();
            StateTreeByPath tree = new(new TrieStoreByPath(new ByPathStateDb(stateDb, LimboLogs.Instance), LimboLogs.Instance), LimboLogs.Instance);
            Assert.That(tree.RootHash, Is.EqualTo(PatriciaTree.EmptyTreeHash));
        }

        [Test]
        public void Can_save_null()
        {
            var a = Nibbles.ToEncodedStorageBytes(new byte[] { 5, 3 });
            var b = Nibbles.ToEncodedStorageBytes(new byte[] { 5, 3, 8 });
            var c = Nibbles.ToEncodedStorageBytes(new byte[] { 5, 3, 0 });
            var d = Nibbles.ToEncodedStorageBytes(new byte[] { 5, 3, 0, 12 });
            var e = Nibbles.ToEncodedStorageBytes(new byte[] { 5, 3, 0, 12, 7 });

            MemColumnsDb<StateColumns> stateDb = new();
            StateTreeByPath tree = new(new TrieStoreByPath(new ByPathStateDb(stateDb, LimboLogs.Instance), LimboLogs.Instance), LimboLogs.Instance);
            tree.Set(TestItem.AddressA, null);
        }

        [Test]
        public void History_update_one_block()
        {
            MemColumnsDb<StateColumns> stateDb = new();
            StateTreeByPath tree = new(new TrieStoreByPath(new ByPathStateDb(stateDb, LimboLogs.Instance), ByPathPersist.IfBlockOlderThan(10), LimboLogs.Instance), LimboLogs.Instance);
            tree.Set(TestItem.AddressA, _account0);
            tree.Commit(0);
            Hash256 root0 = tree.RootHash;
            tree.Set(TestItem.AddressA, _account0.WithChangedBalance(20));
            tree.Commit(1);
            Hash256 root1 = tree.RootHash;
            Account a0 = tree.Get(TestItem.AddressA, root0);
            Account a1 = tree.Get(TestItem.AddressA, root1);

            Assert.That(_account0.Balance, Is.EqualTo(a0.Balance));
            Assert.That(a1.Balance, Is.EqualTo(new UInt256(20)));
        }

        [Test]
        public void History_update_one_block_before_null()
        {
            MemColumnsDb<StateColumns> stateDb = new();
            StateTreeByPath tree = new(new TrieStoreByPath(new ByPathStateDb(stateDb, LimboLogs.Instance), ByPathPersist.IfBlockOlderThan(10), LimboLogs.Instance), LimboLogs.Instance);
            tree.Set(TestItem.AddressB, _account0);
            tree.Commit(0);
            Hash256 root0 = tree.RootHash;
            tree.Set(TestItem.AddressA, _account0);
            tree.Set(TestItem.AddressB, _account0.WithChangedBalance(20));
            tree.Commit(1);
            Hash256 root1 = tree.RootHash;
            Account a0 = tree.Get(TestItem.AddressA, root0);
            Account a1 = tree.Get(TestItem.AddressA, root1);
            Account b1 = tree.Get(TestItem.AddressB, root1);

            Assert.IsNull(a0);
            Assert.That(a1.Balance, Is.EqualTo(new UInt256(0)));
            Assert.That(b1.Balance, Is.EqualTo(new UInt256(20)));
        }


        [Test]
        public void History_update_non_continous_blocks()
        {
            MemColumnsDb<StateColumns> stateDb = new();
            StateTreeByPath tree = new(new TrieStoreByPath(new ByPathStateDb(stateDb, LimboLogs.Instance), ByPathPersist.IfBlockOlderThan(10), LimboLogs.Instance), LimboLogs.Instance);
            tree.Set(TestItem.AddressA, _account0);
            tree.Commit(0);
            Hash256 root0 = tree.RootHash;

            tree.Set(TestItem.AddressB, _account1);
            tree.Commit(1);
            Hash256 root1 = tree.RootHash;

            tree.Set(TestItem.AddressA, _account0.WithChangedBalance(20));
            tree.Commit(2);
            Hash256 root2 = tree.RootHash;

            Account a0_0 = tree.Get(TestItem.AddressA, root0);
            Account a0_1 = tree.Get(TestItem.AddressA, root1);
            Account a0_2 = tree.Get(TestItem.AddressA, root2);

            Assert.That(_account0.Balance, Is.EqualTo(a0_0.Balance));
            Assert.That(_account0.Balance, Is.EqualTo(a0_1.Balance));

            Assert.That(a0_2.Balance, Is.EqualTo(new UInt256(20)));
        }

        [Test]
        public void History_get_cached_from_root_with_no_changes()
        {
            MemColumnsDb<StateColumns> stateDb = new();
            StateTreeByPath tree = new(new TrieStoreByPath(new ByPathStateDb(stateDb, LimboLogs.Instance), ByPathPersist.IfBlockOlderThan(10), LimboLogs.Instance), LimboLogs.Instance);
            tree.Set(TestItem.AddressA, _account0);
            tree.Set(TestItem.AddressB, _account1);
            tree.Set(TestItem.AddressC, _account2);
            tree.Commit(1);
            Hash256 root1 = tree.RootHash;

            tree.Set(TestItem.AddressB, _account1.WithChangedBalance(15));
            tree.Commit(2);
            Hash256 root2 = tree.RootHash;

            tree.Set(TestItem.AddressC, _account2.WithChangedBalance(20));
            tree.Commit(3);
            Hash256 root3 = tree.RootHash;

            Account a0_1 = tree.Get(TestItem.AddressA, root1);
            Account a0_2 = tree.Get(TestItem.AddressA, root2);
            Account a0_3 = tree.Get(TestItem.AddressA, root3);

            Assert.That(a0_1.Balance, Is.EqualTo(_account0.Balance));
            Assert.That(a0_2.Balance, Is.EqualTo(_account0.Balance));
            Assert.That(a0_3.Balance, Is.EqualTo(_account0.Balance));
        }

        [Test]
        public void History_get_on_block_when_account_not_existed()
        {
            MemColumnsDb<StateColumns> stateDb = new();
            StateTreeByPath tree = new(new TrieStoreByPath(new ByPathStateDb(stateDb, LimboLogs.Instance), ByPathPersist.IfBlockOlderThan(10), LimboLogs.Instance), LimboLogs.Instance);
            tree.Set(TestItem.AddressA, _account0);
            tree.Commit(0);
            Hash256 root0 = tree.RootHash;

            tree.Set(TestItem.AddressB, _account1);
            tree.Commit(1);
            Account a1_0 = tree.Get(TestItem.AddressB, root0);
            Assert.IsNull(a1_0);

            tree.Set(TestItem.AddressB, _account2);
            tree.Commit(2);

            a1_0 = tree.Get(TestItem.AddressB, root0);

            Assert.IsNull(a1_0);
        }

        [Test]
        public void History_delete_when_max_number_blocks_exceeded()
        {
            MemColumnsDb<StateColumns> stateDb = new MemColumnsDb<StateColumns>();
            StateTreeByPath tree = new(new TrieStoreByPath(new ByPathStateDb(stateDb, LimboLogs.Instance), ByPathPersist.IfBlockOlderThan(10), LimboLogs.Instance), LimboLogs.Instance);
            tree.Set(TestItem.AddressA, _account0);
            tree.Commit(0);
            Hash256 root0 = tree.RootHash;
            Hash256 root2 = null;

            for (int i = 1; i < 7; i++)
            {
                tree.Set(TestItem.AddressA, _account0.WithChangedBalance((UInt256)i * 5));
                tree.Commit(i);
                if (i == 2)
                    root2 = tree.RootHash;
            }
            Account a1_0 = tree.Get(TestItem.AddressA, root0);
            Account a1_2 = tree.Get(TestItem.AddressA, root2);

            Assert.IsNotNull(a1_0);
            Assert.IsNotNull(a1_2);
            Assert.That(a1_2.Balance, Is.EqualTo((UInt256)(2 * 5)));
        }

        [Test]
        public void Fill_and_empty_tree()
        {
            ILogManager logManager = new NUnitLogManager(LogLevel.Warn);
            ILogger logger = logManager.GetLogger("");

            MemColumnsDb<StateColumns> stateDb = new();
            StateTreeByPath tree = new(new TrieStoreByPath(new ByPathStateDb(stateDb, LimboLogs.Instance), LimboLogs.Instance), LimboLogs.Instance);

            int numberOfAccounts = 10000;

            int seed = Environment.TickCount;
            //int seed = 367667468;
            logger.Warn($"Seed: {seed}");
            Random r = new Random(seed);

            Hash256[] allPaths = new Hash256[numberOfAccounts];

            for (int i = 0; i < numberOfAccounts; i++)
            {
                byte[] key = new byte[32];
                r.NextBytes(key);
                Hash256 keccak = new(key);
                allPaths[i] = keccak;

                tree.Set(keccak, TestItem.GenerateRandomAccount());
            }
            tree.Commit(0);

            Hash256 root0 = tree.RootHash;

            for (int i = 0; i < numberOfAccounts; i++)
            {
                tree.Set(allPaths[i], null);
            }
            tree.Commit(1);

            Hash256 rootEnd = tree.RootHash;

            MemDb stateColumns = (MemDb)stateDb.GetColumnDb(StateColumns.State);
            Assert.That(stateColumns.GetAllValues().All(b => b is null), Is.True);
            //Assert.That(rootEnd, Is.EqualTo(Keccak.EmptyTreeHash));
        }

        [Test]
        public void CopyStateTest()
        {
            MemColumnsDb<StateColumns> stateDb = new();
            StateTreeByPath tree = new(new TrieStoreByPath(new ByPathStateDb(stateDb, LimboLogs.Instance), LimboLogs.Instance), LimboLogs.Instance);

            tree.Set(TestItem.AddressA, _account1);
            tree.Set(TestItem.AddressB, _account1);
            tree.Set(TestItem.AddressC, _account1);
            tree.Set(TestItem.AddressD, _account1);
            tree.Set(TestItem.AddressA, null);
            tree.Commit(0);
            tree.Get(TestItem.AddressA).Should().BeNull();
            tree.Get(TestItem.AddressB).Balance.Should().BeEquivalentTo(_account1.Balance);
            tree.Get(TestItem.AddressC).Balance.Should().BeEquivalentTo(_account1.Balance);
            tree.Get(TestItem.AddressD).Balance.Should().BeEquivalentTo(_account1.Balance);
        }

        [Test, Explicit]
        public void Process_block_by_block_and_compare_tries_on_different_storage()
        {
            ILogManager logManager = new NUnitLogManager(LogLevel.Warn);
            ILogger logger = logManager.GetLogger("");

            MemColumnsDb<StateColumns> pathDb = new();
            TrieStoreByPath pathStore = new(new ByPathStateDb(pathDb, LimboLogs.Instance), ByPathPersist.IfBlockOlderThan(10), LimboLogs.Instance);
            StateTreeByPath tree = new(pathStore, LimboLogs.Instance);

            MemDb db = new MemDb();
            StateTree hashStateTree = new(new TrieStore(db, logManager), logManager);

            int numberOfAccounts = 10000;
            int numberOfBlocks = 100;
            int numberOfUpdates = numberOfAccounts / 10;

            int seed = Environment.TickCount;
            //int seed = 185189906;
            logger.Warn($"Seed: {seed}");
            Random _random = new Random(seed);

            Hash256[] allPaths = new Hash256[numberOfAccounts];

            for (int i = 0; i < numberOfAccounts; i++)
            {
                byte[] key = new byte[32];
                _random.NextBytes(key);
                Hash256 keccak = new(key);
                allPaths[i] = keccak;

                Account newAccount = TestItem.GenerateRandomAccount();
                tree.Set(keccak, newAccount);
                hashStateTree.Set(keccak, newAccount);
            }
            tree.Commit(0);
            hashStateTree.Commit(0);

            Assert.That(tree.RootHash, Is.EqualTo(hashStateTree.RootHash));
            CompareTrees(hashStateTree, tree);

            Hash256 prevRootHash = tree.RootHash;
            for (int i = 1; i <= numberOfBlocks; i++)
            {
                tree.RootHash = prevRootHash;
                tree.ParentStateRootHash = prevRootHash;
                for (int accountIndex = 0; accountIndex < numberOfUpdates; accountIndex++)
                {
                    Account account = TestItem.GenerateRandomAccount();
                    Hash256 path = allPaths[_random.Next(numberOfAccounts - 1)];

                    if (hashStateTree.Get(path) is not null)
                    {
                        if (_random.NextSingle() > 0.25)
                        {
                            tree.Set(path, account);
                            hashStateTree.Set(path, account);
                        }
                        else
                        {
                            tree.Set(path, null);
                            hashStateTree.Set(path, null);
                        }
                    }
                    else
                    {
                        tree.Set(path, account);
                        hashStateTree.Set(path, account);
                    }
                }

                tree.Commit(i);
                hashStateTree.Commit(i);

                Assert.That(tree.RootHash, Is.EqualTo(hashStateTree.RootHash));
                CompareTrees(hashStateTree, tree);

                prevRootHash = tree.RootHash;
            }
        }

        [Test]
        public void Get_By_Path_With_Cache_No_Reset_No_Root_Overwrite()
        {
            ILogManager logManager = new NUnitLogManager(LogLevel.Warn);
            ILogger logger = logManager.GetLogger("");

            MemColumnsDb<StateColumns> pathDb = new();
            TrieStoreByPath pathStore = new(new ByPathStateDb(pathDb, LimboLogs.Instance), ByPathPersist.IfBlockOlderThan(2), logManager);
            StateTreeByPath tree = new(pathStore, LimboLogs.Instance);
            MemDb innerStateDb = (MemDb)pathDb.GetColumnDb(StateColumns.State);

            tree.Set(TestItem.AddressA, TestItem.GenerateIndexedAccount(100));
            tree.Set(TestItem.AddressB, TestItem.GenerateIndexedAccount(200));
            tree.Commit(1);
            Hash256 root_1 = tree.RootHash;

            Assert.That(tree.Get(TestItem.AddressA).Balance, Is.EqualTo((UInt256)100));
            Assert.That(tree.Get(TestItem.AddressB).Balance, Is.EqualTo((UInt256)200));

            Assert.That(innerStateDb.ReadsCount, Is.EqualTo(0));

            tree.Set(TestItem.AddressC, TestItem.GenerateIndexedAccount(300));
            tree.Commit(2); //persist here
            Hash256 root_2 = tree.RootHash;

            Assert.That(tree.Get(TestItem.AddressA).Balance, Is.EqualTo((UInt256)100));
            Assert.That(tree.Get(TestItem.AddressC).Balance, Is.EqualTo((UInt256)300));

            Assert.That(innerStateDb.ReadsCount, Is.EqualTo(2));
        }

        [Test]
        public void Get_By_Path_With_Cache_With_Reset_At_Persisted_With_Root_Overwrite()
        {
            ILogManager logManager = new NUnitLogManager(LogLevel.Warn);
            ILogger logger = logManager.GetLogger("");

            MemColumnsDb<StateColumns> pathDb = new();
            TrieStoreByPath pathStore = new(new ByPathStateDb(pathDb, LimboLogs.Instance), ByPathPersist.IfBlockOlderThan(2), logManager);
            StateTreeByPath tree = new(pathStore, LimboLogs.Instance);
            MemDb innerStateDb = (MemDb)pathDb.GetColumnDb(StateColumns.State);

            tree.Set(TestItem.AddressA, TestItem.GenerateIndexedAccount(100));
            tree.Set(TestItem.AddressB, TestItem.GenerateIndexedAccount(200));
            tree.Commit(1);
            Hash256 root_1 = tree.RootHash;

            tree.Set(TestItem.AddressC, TestItem.GenerateIndexedAccount(300));

            Assert.That(tree.Get(TestItem.AddressA).Balance, Is.EqualTo((UInt256)100));
            Assert.That(tree.Get(TestItem.AddressB).Balance, Is.EqualTo((UInt256)200));
            Assert.That(tree.Get(TestItem.AddressC).Balance, Is.EqualTo((UInt256)300));

            //nothing read from DB - all from cache of from in-mem trie
            Assert.That(innerStateDb.ReadsCount, Is.EqualTo(0));

            tree.Commit(2); //persist here
            Hash256 root_2 = tree.RootHash;

            //reset the trie and set context to latest root hash
            tree = new StateTreeByPath(pathStore, logManager);
            tree.RootHash = root_2;

            Assert.That(tree.Get(TestItem.AddressA).Balance, Is.EqualTo((UInt256)100));
            Assert.That(tree.Get(TestItem.AddressC).Balance, Is.EqualTo((UInt256)300));
            //both read from database
            Assert.That(innerStateDb.ReadsCount, Is.EqualTo(2));

            //latest persisted block was 2, so we don't have history at block 1
            Assert.That(() => tree.Get(TestItem.AddressA, root_1), Throws.TypeOf<InvalidOperationException>());
        }

        [Test]
        public void Get_By_Path_With_Cache_With_Reset_Not_At_Persisted_With_Root_Overwrite()
        {
            ILogManager logManager = new NUnitLogManager(LogLevel.Warn);
            ILogger logger = logManager.GetLogger("");

            MemColumnsDb<StateColumns> pathDb = new();
            TrieStoreByPath pathStore = new(new ByPathStateDb(pathDb, logManager), ByPathPersist.IfBlockOlderThan(4), logManager);
            StateTreeByPath tree = new(pathStore, LimboLogs.Instance);
            MemDb innerStateDb = (MemDb)pathDb.GetColumnDb(StateColumns.State);

            //block 0
            tree.Set(TestItem.AddressA, TestItem.GenerateIndexedAccount(100));
            tree.Set(TestItem.AddressB, TestItem.GenerateIndexedAccount(200));
            tree.Commit(0); //block 0 is persisted
            Hash256 root_0 = tree.RootHash;

            //block 1
            tree.Set(TestItem.AddressA, TestItem.GenerateIndexedAccount(101));
            tree.Set(TestItem.AddressC, TestItem.GenerateIndexedAccount(301));
            int expectedReads = 3; //reads for intermmediate nodes when traversing trie in Set operation

            Assert.That(tree.Get(TestItem.AddressA).Balance, Is.EqualTo((UInt256)101));
            Assert.That(tree.Get(TestItem.AddressB).Balance, Is.EqualTo((UInt256)200));
            Assert.That(tree.Get(TestItem.AddressC).Balance, Is.EqualTo((UInt256)301));

            //1 accounts read from database, 2 from trie (modifications not yet commited)
            expectedReads++;
            Assert.That(innerStateDb.ReadsCount, Is.EqualTo(expectedReads));

            tree.Commit(1); //not persisted
            Hash256 root_1 = tree.RootHash;

            //reset the trie and set context to latest root hash (block 1 not persisted)
            tree = new StateTreeByPath(pathStore, logManager);
            pathStore.OpenContext(2, root_1);
            tree.RootHash = root_1;

            Assert.That(tree.Get(TestItem.AddressA).Balance, Is.EqualTo((UInt256)101));
            Assert.That(tree.Get(TestItem.AddressC).Balance, Is.EqualTo((UInt256)301));
            //both reads from cache
            Assert.That(innerStateDb.ReadsCount, Is.EqualTo(expectedReads));

            //can return for root_0 as it's the last persisted - both reads from cache
            expectedReads += 2 * 2;
            Assert.That(tree.Get(TestItem.AddressA, root_0).Balance, Is.EqualTo((UInt256)100));
            Assert.That(tree.Get(TestItem.AddressB, root_0).Balance, Is.EqualTo((UInt256)200));
            Assert.That(innerStateDb.ReadsCount, Is.EqualTo(expectedReads));

            //block 2
            tree.Set(TestItem.AddressA, TestItem.GenerateIndexedAccount(102));
            tree.Set(TestItem.AddressC, TestItem.GenerateIndexedAccount(302));

            tree.Commit(2); //not persisted
            Hash256 root_2 = tree.RootHash;

            //reset the trie and set context to latest root hash (block 1 not persisted)
            tree = new StateTreeByPath(pathStore, logManager);
            pathStore.OpenContext(3, root_1);
            tree.RootHash = root_1;

            //get items at current root hash - data at block 1 - all reads from cache
            Assert.That(tree.Get(TestItem.AddressA).Balance, Is.EqualTo((UInt256)101));
            Assert.That(tree.Get(TestItem.AddressC).Balance, Is.EqualTo((UInt256)301));
            Assert.That(innerStateDb.ReadsCount, Is.EqualTo(expectedReads));

            //get items at overwritten root hash pointing to block 2 - all reads from cache
            Assert.That(tree.Get(TestItem.AddressA, root_2).Balance, Is.EqualTo((UInt256)102));
            Assert.That(tree.Get(TestItem.AddressC, root_2).Balance, Is.EqualTo((UInt256)302));
            Assert.That(innerStateDb.ReadsCount, Is.EqualTo(expectedReads));
        }

        private void CompareTrees(StateTree keccakTree, StateTreeByPath pathTree)
        {
            TreeDumper dumper = new TreeDumper();
            keccakTree.Accept(dumper, keccakTree.RootHash);
            string remote = dumper.ToString();

            dumper.Reset();

            pathTree.Accept(dumper, pathTree.RootHash);
            string local = dumper.ToString();

            Assert.That(local, Is.EqualTo(remote), $"{remote}{Environment.NewLine}{local}");
        }
    }
}
