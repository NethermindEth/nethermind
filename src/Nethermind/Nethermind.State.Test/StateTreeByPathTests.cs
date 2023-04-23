// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
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
            MemDb db = new();
            StateTreeByPath tree = new(new TrieStoreByPath(db, LimboLogs.Instance), LimboLogs.Instance);
            tree.Set(TestItem.AddressA, _account0);
            tree.Set(TestItem.AddressB, _account0);
            tree.Set(TestItem.AddressC, _account0);
            tree.Commit(0);
            Assert.AreEqual(0, db.ReadsCount, "reads");
        }

        [Test]
        public void Minimal_writes_when_setting_on_empty()
        {
            MemDb db = new();
            StateTreeByPath tree = new(new TrieStoreByPath(db, LimboLogs.Instance), LimboLogs.Instance);
            tree.Set(TestItem.AddressA, _account0);
            tree.Set(TestItem.AddressB, _account0);
            tree.Set(TestItem.AddressC, _account0);
            tree.Commit(0);
            Assert.AreEqual(8, db.WritesCount, "writes"); // branch, branch, two leaves (one is stored as RLP)
        }

        [Test]
        public void Minimal_writes_when_setting_on_empty_scenario_2()
        {
            MemDb db = new();
            StateTreeByPath tree = new(new TrieStoreByPath(db, NUnitLogManager.Instance), NUnitLogManager.Instance);
            tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), _account0);
            tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb0"), _account0);
            tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb1"), _account0);
            tree.Commit(0);

            tree.Get(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000")).Should().BeEquivalentTo(_account0);
            tree.Get(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb0")).Should().BeEquivalentTo(_account0);
            tree.Get(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb1")).Should().BeEquivalentTo(_account0);
            Assert.AreEqual(10, db.WritesCount, "writes"); // extension, branch, leaf, extension, branch, 2x same leaf
            Assert.AreEqual(7, Trie.Metrics.TreeNodeHashCalculations, "hashes");
            Assert.AreEqual(7, Trie.Metrics.TreeNodeRlpEncodings, "encodings");
        }

        // [Test]
        // public void Minimal_writes_when_setting_on_empty_scenario_3()
        // {
        //     MemDb db = new();
        //     StateTreeByPath tree = new(new TrieStoreByPath(db, LimboLogs.Instance), LimboLogs.Instance);
        //     tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), _account0);
        //     tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb0"), _account0);
        //     tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb1"), _account0);
        //     tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb1"), null);
        //     tree.Commit(0);
        //     Assert.AreEqual(6, db.WritesCount, "writes"); // extension, branch, 2x leaf
        //     Assert.AreEqual(4, Trie.Metrics.TreeNodeHashCalculations, "hashes");
        //     Assert.AreEqual(4, Trie.Metrics.TreeNodeRlpEncodings, "encodings");
        // }

        // [Test]
        // public void Minimal_writes_when_setting_on_empty_scenario_4()
        // {
        //     MemDb db = new();
        //     StateTreeByPath tree = new(new TrieStoreByPath(db, LimboLogs.Instance), LimboLogs.Instance);
        //     tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), _account0);
        //     tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb0"), _account0);
        //     tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb1"), _account0);
        //     tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb0"), null);
        //     tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb1"), null);
        //     tree.Commit(0);
        //     Assert.AreEqual(2, db.WritesCount, "writes"); // extension, branch, 2x leaf
        //     Assert.AreEqual(1, Trie.Metrics.TreeNodeHashCalculations, "hashes");
        //     Assert.AreEqual(1, Trie.Metrics.TreeNodeRlpEncodings, "encodings");
        // }

        [Test]
        public void Minimal_writes_when_setting_on_empty_scenario_5()
        {
            MemDb db = new();
            StateTreeByPath tree = new(new TrieStoreByPath(db, LimboLogs.Instance), LimboLogs.Instance);
            tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), _account0);
            tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb0"), _account0);
            tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb1"), _account0);
            tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb0"), null);
            tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb1"), null);
            tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), null);
            tree.Commit(0);
            Assert.AreEqual(0, db.WritesCount, "writes"); // extension, branch, 2x leaf
            Assert.AreEqual(0, Trie.Metrics.TreeNodeHashCalculations, "hashes");
            Assert.AreEqual(0, Trie.Metrics.TreeNodeRlpEncodings, "encodings");
        }

        [Test]
        public void Scenario_traverse_extension_read_full_match()
        {
            MemDb db = new();
            StateTreeByPath tree = new(new TrieStoreByPath(db, LimboLogs.Instance), LimboLogs.Instance);
            tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), _account0);
            tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111111"), _account1);
            Account account = tree.Get(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111111"));
            //Assert.AreEqual(0, db.ReadsCount);
            Assert.AreEqual(_account1.Balance, account.Balance);
            tree.UpdateRootHash();
            Keccak rootHash = tree.RootHash;
            Assert.AreEqual("0xf99f1d3234bad8d63d818db36ff63eefc8916263e654db8b800d3bd03f6339a5", rootHash.ToString(true));
            tree.Commit(0);
            Assert.AreEqual("0xf99f1d3234bad8d63d818db36ff63eefc8916263e654db8b800d3bd03f6339a5", rootHash.ToString(true));
        }

        [Test]
        public void Scenario_traverse_extension_read_missing()
        {
            MemDb db = new();
            StateTreeByPath tree = new(new TrieStoreByPath(db, LimboLogs.Instance), LimboLogs.Instance);
            tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), _account0);
            tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111111"), _account1);
            Account account = tree.Get(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeedddddddddddddddddddddddd"));
            Assert.Null(account);
            tree.UpdateRootHash();
            Keccak rootHash = tree.RootHash;
            Assert.AreEqual("0xf99f1d3234bad8d63d818db36ff63eefc8916263e654db8b800d3bd03f6339a5", rootHash.ToString(true));
            tree.Commit(0);
            Assert.AreEqual("0xf99f1d3234bad8d63d818db36ff63eefc8916263e654db8b800d3bd03f6339a5", rootHash.ToString(true));
        }

        [Test]
        public void Scenario_traverse_extension_new_branching()
        {
            MemDb db = new();
            StateTreeByPath tree = new(new TrieStoreByPath(db, LimboLogs.Instance), LimboLogs.Instance);
            tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), _account0);
            tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111111"), _account1);
            tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeedddddddddddddddddddddddd"), _account2);
            tree.UpdateRootHash();
            Keccak rootHash = tree.RootHash;
            Assert.AreEqual("0x543c960143a2a06b685d6b92f0c37000273e616bc23888521e7edf15ad06da46", rootHash.ToString(true));
            tree.Commit(0);
            Assert.AreEqual("0x543c960143a2a06b685d6b92f0c37000273e616bc23888521e7edf15ad06da46", rootHash.ToString(true));
        }

        [Test]
        public void Scenario_traverse_extension_delete_missing()
        {
            MemDb db = new();
            StateTreeByPath tree = new(new TrieStoreByPath(db, LimboLogs.Instance), LimboLogs.Instance);
            tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), _account0);
            tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111111"), _account1);
            tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeddddddddddddddddddddddddd"), null);
            Assert.AreEqual(0, db.ReadsCount);
            tree.UpdateRootHash();
            Keccak rootHash = tree.RootHash;
            Assert.AreEqual("0xf99f1d3234bad8d63d818db36ff63eefc8916263e654db8b800d3bd03f6339a5", rootHash.ToString(true));
            tree.Commit(0);
            Assert.AreEqual("0xf99f1d3234bad8d63d818db36ff63eefc8916263e654db8b800d3bd03f6339a5", rootHash.ToString(true));
        }

        [Test]
        public void Scenario_traverse_extension_create_new_extension()
        {
            MemDb db = new();
            StateTreeByPath tree = new(new TrieStoreByPath(db, LimboLogs.Instance), LimboLogs.Instance);
            tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), _account0);
            tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111111"), _account1);
            tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeaaaaaaaaaaaaaaaab00000000"), _account2);
            tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeaaaaaaaaaaaaaaaab11111111"), _account3);
            Assert.AreEqual(0, db.ReadsCount);
            tree.UpdateRootHash();
            Keccak rootHash = tree.RootHash;
            Assert.AreEqual("0x0918112fc898173562441709a2c1cbedb80d1aaecaeadf2f3e9492eeaa568c67", rootHash.ToString(true));
            tree.Commit(0);
            Assert.AreEqual("0x0918112fc898173562441709a2c1cbedb80d1aaecaeadf2f3e9492eeaa568c67", rootHash.ToString(true));
        }

        [Test]
        public void Scenario_traverse_leaf_update_new_value()
        {
            MemDb db = new();
            StateTreeByPath tree = new(new TrieStoreByPath(db, LimboLogs.Instance), LimboLogs.Instance);
            tree.Set(new Keccak("1111111111111111111111111111111111111111111111111111111111111111"), _account0);
            tree.Set(new Keccak("1111111111111111111111111111111111111111111111111111111111111111"), _account1);
            tree.UpdateRootHash();
            Keccak rootHash = tree.RootHash;
            Assert.AreEqual("0xaa5c248d4b4b8c27a654296a8e0cc51131eb9011d9166fa0fca56a966489e169", rootHash.ToString(true));
            tree.Commit(0);
            Assert.AreEqual("0xaa5c248d4b4b8c27a654296a8e0cc51131eb9011d9166fa0fca56a966489e169", rootHash.ToString(true));
        }

        [Test]
        public void Scenario_traverse_leaf_update_no_change()
        {
            MemDb db = new();
            StateTreeByPath tree = new(new TrieStoreByPath(db, LimboLogs.Instance), LimboLogs.Instance);
            tree.Set(new Keccak("1111111111111111111111111111111111111111111111111111111111111111"), _account0);
            tree.Set(new Keccak("1111111111111111111111111111111111111111111111111111111111111111"), _account0);
            tree.UpdateRootHash();
            Keccak rootHash = tree.RootHash;
            Assert.AreEqual("0x491fbb33aaff22c0a7ff68d5c81ec114dddf89d022ccdee838a0e9d6cd45cab4", rootHash.ToString(true));
            tree.Commit(0);
            Assert.AreEqual("0x491fbb33aaff22c0a7ff68d5c81ec114dddf89d022ccdee838a0e9d6cd45cab4", rootHash.ToString(true));
        }

        [Test]
        public void Scenario_traverse_leaf_read_matching_leaf()
        {
            MemDb db = new();
            StateTreeByPath tree = new(new TrieStoreByPath(db, LimboLogs.Instance), LimboLogs.Instance);
            tree.Set(new Keccak("1111111111111111111111111111111111111111111111111111111111111111"), _account0);
            tree.Set(new Keccak("1111111111111111111111111111111111111111111111111111111111111111"), null);
            tree.UpdateRootHash();
            Keccak rootHash = tree.RootHash;
            Assert.AreEqual("0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421", rootHash.ToString(true));
            tree.Commit(0);
            Assert.AreEqual("0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421", rootHash.ToString(true));
        }

        [Test]
        public void Scenario_traverse_leaf_delete_missing()
        {
            MemDb db = new();
            StateTreeByPath tree = new(new TrieStoreByPath(db, LimboLogs.Instance), LimboLogs.Instance);
            tree.Set(new Keccak("1111111111111111111111111111111111111111111111111111111111111111"), _account0);
            tree.Set(new Keccak("1111111111111111111111111111111ddddddddddddddddddddddddddddddddd"), null);
            tree.UpdateRootHash();
            Keccak rootHash = tree.RootHash;
            Assert.AreEqual("0x491fbb33aaff22c0a7ff68d5c81ec114dddf89d022ccdee838a0e9d6cd45cab4", rootHash.ToString(true));
            tree.Commit(0);
            Assert.AreEqual("0x491fbb33aaff22c0a7ff68d5c81ec114dddf89d022ccdee838a0e9d6cd45cab4", rootHash.ToString(true));
        }

        [Test]
        public void Scenario_traverse_leaf_update_with_extension()
        {
            MemDb db = new();
            StateTreeByPath tree = new(new TrieStoreByPath(db, LimboLogs.Instance), LimboLogs.Instance);
            tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111111111111111111111111111111"), _account0);
            tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000000000000000000000000000"), _account1);
            tree.UpdateRootHash();
            Keccak rootHash = tree.RootHash;
            Assert.AreEqual("0x215a4bab4cf2d5ebbaa59c82ae94c9707fcf4cc0ca1fe7e18f918e46db428ef9", rootHash.ToString(true));
            tree.Commit(0);
            Assert.AreEqual("0x215a4bab4cf2d5ebbaa59c82ae94c9707fcf4cc0ca1fe7e18f918e46db428ef9", rootHash.ToString(true));
        }

        [Test]
        public void Scenario_traverse_leaf_delete_matching_leaf()
        {
            MemDb db = new();
            StateTreeByPath tree = new(new TrieStoreByPath(db, LimboLogs.Instance), LimboLogs.Instance);
            tree.Set(new Keccak("1111111111111111111111111111111111111111111111111111111111111111"), _account0);
            Account account = tree.Get(new Keccak("1111111111111111111111111111111111111111111111111111111111111111"));
            Assert.NotNull(account);
            tree.UpdateRootHash();
            Keccak rootHash = tree.RootHash;
            Assert.AreEqual("0x491fbb33aaff22c0a7ff68d5c81ec114dddf89d022ccdee838a0e9d6cd45cab4", rootHash.ToString(true));
            tree.Commit(0);
            Assert.AreEqual("0x491fbb33aaff22c0a7ff68d5c81ec114dddf89d022ccdee838a0e9d6cd45cab4", rootHash.ToString(true));
        }

        [Test]
        public void Scenario_traverse_leaf_read_missing()
        {
            MemDb db = new();
            StateTreeByPath tree = new(new TrieStoreByPath(db, LimboLogs.Instance), LimboLogs.Instance);
            tree.Set(new Keccak("1111111111111111111111111111111111111111111111111111111111111111"), _account0);
            Account account = tree.Get(new Keccak("111111111111111111111111111111111111111111111111111111111ddddddd"));
            Assert.Null(account);
            tree.UpdateRootHash();
            Keccak rootHash = tree.RootHash;
            Assert.AreEqual("0x491fbb33aaff22c0a7ff68d5c81ec114dddf89d022ccdee838a0e9d6cd45cab4", rootHash.ToString(true));
            tree.Commit(0);
            Assert.AreEqual("0x491fbb33aaff22c0a7ff68d5c81ec114dddf89d022ccdee838a0e9d6cd45cab4", rootHash.ToString(true));
        }

        [Test]
        public void Scenario_traverse_branch_update_missing()
        {
            MemDb db = new();
            StateTreeByPath tree = new(new TrieStoreByPath(db, LimboLogs.Instance), LimboLogs.Instance);
            tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000"), _account0);
            tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111"), _account1);
            tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb22222"), _account2);
            tree.UpdateRootHash();
            Keccak rootHash = tree.RootHash;
            Assert.AreEqual("0xc063af0bd3dd88320bc852ff8452049c42fbc06d1a69661567bd427572824cbf", rootHash.ToString(true));
            tree.Commit(0);
            Assert.AreEqual("0xc063af0bd3dd88320bc852ff8452049c42fbc06d1a69661567bd427572824cbf", rootHash.ToString(true));
        }

        [Test]
        public void Scenario_traverse_branch_read_missing()
        {
            MemDb db = new();
            StateTreeByPath tree = new(new TrieStoreByPath(db, LimboLogs.Instance), LimboLogs.Instance);
            tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000"), _account0);
            tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111"), _account1);
            Account account = tree.Get(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb22222"));
            Assert.Null(account);
            tree.UpdateRootHash();
            Keccak rootHash = tree.RootHash;
            Assert.AreEqual("0x94a193704e99c219d9a21428eb37d6d2d71b3d2cea80c77ff0e201c0df70a283", rootHash.ToString(true));
            tree.Commit(0);
            Assert.AreEqual("0x94a193704e99c219d9a21428eb37d6d2d71b3d2cea80c77ff0e201c0df70a283", rootHash.ToString(true));
        }

        [Test]
        public void Scenario_traverse_branch_delete_missing()
        {
            MemDb db = new();
            StateTreeByPath tree = new(new TrieStoreByPath(db, LimboLogs.Instance), LimboLogs.Instance);
            tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000"), _account0);
            tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111"), _account1);
            tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb22222"), null);
            tree.UpdateRootHash();
            Keccak rootHash = tree.RootHash;
            Assert.AreEqual("0x94a193704e99c219d9a21428eb37d6d2d71b3d2cea80c77ff0e201c0df70a283", rootHash.ToString(true));
            tree.Commit(0);
            Assert.AreEqual("0x94a193704e99c219d9a21428eb37d6d2d71b3d2cea80c77ff0e201c0df70a283", rootHash.ToString(true));
        }

        [Test]
        public void Minimal_hashes_when_setting_on_empty()
        {
            MemDb db = new();
            StateTreeByPath tree = new(new TrieStoreByPath(db, Prune.WhenCacheReaches(250.MiB()), Persist.EveryBlock, LimboLogs.Instance), LimboLogs.Instance);
            //StateTreeByPath tree = new(new TrieStoreByPath(db, LimboLogs.Instance), LimboLogs.Instance);
            tree.Set(TestItem.AddressA, _account0);
            tree.Set(TestItem.AddressB, _account0);
            tree.Set(TestItem.AddressC, _account0);
            tree.Commit(0);
            tree.Get(TestItem.AddressA);
            tree.Get(TestItem.AddressB);
            tree.Get(TestItem.AddressC);
            Assert.AreEqual(5, Trie.Metrics.TreeNodeHashCalculations, "hashes"); // branch, branch, three leaves
        }

        [Test]
        public void Minimal_encodings_when_setting_on_empty()
        {
            MemDb db = new();
            StateTreeByPath tree = new(new TrieStoreByPath(db, LimboLogs.Instance), LimboLogs.Instance);
            tree.Set(TestItem.AddressA, _account0);
            tree.Set(TestItem.AddressB, _account0);
            tree.Set(TestItem.AddressC, _account0);
            tree.Commit(0);
            Assert.AreEqual(5, Trie.Metrics.TreeNodeRlpEncodings, "encodings"); // branch, branch, three leaves
        }

        [Test]
        public void Zero_decodings_when_setting_on_empty()
        {
            MemDb db = new();
            StateTreeByPath tree = new(new TrieStoreByPath(db, LimboLogs.Instance), LimboLogs.Instance);
            tree.Set(TestItem.AddressA, _account0);
            tree.Set(TestItem.AddressB, _account0);
            tree.Set(TestItem.AddressC, _account0);
            tree.Commit(0);
            Assert.AreEqual(0, Trie.Metrics.TreeNodeRlpDecodings, "decodings");
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
            MemDb db = new();
            StateTreeByPath tree = new(new TrieStoreByPath(db, LimboLogs.Instance), LimboLogs.Instance);
            tree.Set(TestItem.AddressA, _account0);
            tree.Commit(0);
            Assert.AreEqual(1, db.WritesCount, "writes before"); // extension, branch, two leaves
            tree.Set(TestItem.AddressA, _account1);
            tree.Set(TestItem.AddressA, _account0);
            tree.Commit(0);
            Assert.AreEqual(1, db.WritesCount, "writes after"); // extension, branch, two leaves
        }

        [Test]
        public void No_writes_without_commit()
        {
            MemDb db = new();
            StateTreeByPath tree = new(new TrieStoreByPath(db, LimboLogs.Instance), LimboLogs.Instance);
            tree.Set(TestItem.AddressA, _account0);
            Assert.AreEqual(0, db.WritesCount, "writes");
        }

        [Test]
        public void Can_ask_about_root_hash_without_commiting()
        {
            MemDb db = new();
            StateTreeByPath tree = new(new TrieStoreByPath(db, LimboLogs.Instance), LimboLogs.Instance);
            tree.Set(TestItem.AddressA, _account0);
            tree.UpdateRootHash();
            Assert.AreEqual("0x545a417202afcb10925b2afddb70a698710bb1cf4ab32942c42e9f019d564fdc", tree.RootHash.ToString(true));
        }

        [Test]
        public void Can_ask_about_root_hash_without_when_emptied()
        {
            MemDb db = new();
            StateTreeByPath tree = new(new TrieStoreByPath(db, LimboLogs.Instance), LimboLogs.Instance);
            tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), _account0);
            tree.UpdateRootHash();
            Assert.AreNotEqual(PatriciaTree.EmptyTreeHash, tree.RootHash);
            tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb0"), _account0);
            tree.UpdateRootHash();
            Assert.AreNotEqual(PatriciaTree.EmptyTreeHash, tree.RootHash);
            tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb1"), _account0);
            tree.UpdateRootHash();
            Assert.AreNotEqual(PatriciaTree.EmptyTreeHash, tree.RootHash);
            tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb0"), null);
            tree.UpdateRootHash();
            Assert.AreNotEqual(PatriciaTree.EmptyTreeHash, tree.RootHash);
            tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb1"), null);
            tree.UpdateRootHash();
            Assert.AreNotEqual(PatriciaTree.EmptyTreeHash, tree.RootHash);
            tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), null);
            tree.UpdateRootHash();
            Assert.AreEqual(PatriciaTree.EmptyTreeHash, tree.RootHash);
            tree.Commit(0);
            Assert.AreEqual(PatriciaTree.EmptyTreeHash, tree.RootHash);
        }

        [Test]
        public void hash_empty_tree_root_hash_initially()
        {
            MemDb db = new();
            StateTreeByPath tree = new(new TrieStoreByPath(db, LimboLogs.Instance), LimboLogs.Instance);
            Assert.AreEqual(PatriciaTree.EmptyTreeHash, tree.RootHash);
        }

        [Test]
        public void Can_save_null()
        {
            var a = Nibbles.ToEncodedStorageBytes(new byte[] { 5, 3 });
            var b = Nibbles.ToEncodedStorageBytes(new byte[] { 5, 3, 8 });
            var c = Nibbles.ToEncodedStorageBytes(new byte[] { 5, 3, 0 });
            var d = Nibbles.ToEncodedStorageBytes(new byte[] { 5, 3, 0, 12 });
            var e = Nibbles.ToEncodedStorageBytes(new byte[] { 5, 3, 0, 12, 7});

            MemDb db = new();
            StateTreeByPath tree = new(new TrieStoreByPath(db, LimboLogs.Instance), LimboLogs.Instance);
            tree.Set(TestItem.AddressA, null);
        }

        [Test]
        public void History_update_one_block()
        {
            MemDb db = new();
            StateTreeByPath tree = new(new TrieStoreByPath(db, No.Pruning, Persist.EveryBlock, LimboLogs.Instance), LimboLogs.Instance);
            tree.Set(TestItem.AddressA, _account0);
            tree.Commit(0);
            Keccak root0 = tree.RootHash;
            tree.Set(TestItem.AddressA, _account0.WithChangedBalance(20));
            tree.Commit(1);
            Keccak root1 = tree.RootHash;
            Account a0 = tree.Get(TestItem.AddressA, root0);
            Account a1 = tree.Get(TestItem.AddressA, root1);

            Assert.AreEqual(a0.Balance, _account0.Balance);
            Assert.AreEqual(new UInt256(20), a1.Balance);
        }

        [Test]
        public void History_update_one_block_before_null()
        {
            MemDb db = new();
            StateTreeByPath tree = new(new TrieStoreByPath(db, No.Pruning, Persist.EveryBlock, LimboLogs.Instance), LimboLogs.Instance);
            tree.Set(TestItem.AddressB, _account0);
            tree.Commit(0);
            Keccak root0 = tree.RootHash;
            tree.Set(TestItem.AddressA, _account0);
            tree.Set(TestItem.AddressB, _account0.WithChangedBalance(20));
            tree.Commit(1);
            Keccak root1 = tree.RootHash;
            Account a0 = tree.Get(TestItem.AddressA, root0);
            Account a1 = tree.Get(TestItem.AddressA, root1);
            Account b1 = tree.Get(TestItem.AddressB, root1);

            Assert.IsNull(a0);
            Assert.AreEqual(new UInt256(0), a1.Balance);
            Assert.AreEqual(new UInt256(20), b1.Balance);
        }


        [Test]
        public void History_update_non_continous_blocks()
        {
            MemDb db = new();
            StateTreeByPath tree = new(new TrieStoreByPath(db, No.Pruning, Persist.EveryBlock, LimboLogs.Instance), LimboLogs.Instance);
            tree.Set(TestItem.AddressA, _account0);
            tree.Commit(0);
            Keccak root0 = tree.RootHash;

            tree.Set(TestItem.AddressB, _account1);
            tree.Commit(1);
            Keccak root1 = tree.RootHash;

            tree.Set(TestItem.AddressA, _account0.WithChangedBalance(20));
            tree.Commit(2);
            Keccak root2 = tree.RootHash;

            Account a0_0 = tree.Get(TestItem.AddressA, root0);
            Account a0_1 = tree.Get(TestItem.AddressA, root1);
            Account a0_2 = tree.Get(TestItem.AddressA, root2);

            Assert.AreEqual(a0_0.Balance, _account0.Balance);
            Assert.AreEqual(a0_1.Balance, _account0.Balance);

            Assert.AreEqual(new UInt256(20), a0_2.Balance);
        }

        [Test]
        public void History_get_on_block_when_account_not_existed()
        {
            MemDb db = new();
            StateTreeByPath tree = new(new TrieStoreByPath(db, No.Pruning, Persist.EveryBlock, LimboLogs.Instance, 1), LimboLogs.Instance);
            tree.Set(TestItem.AddressA, _account0);
            tree.Commit(0);
            Keccak root0 = tree.RootHash;

            tree.Set(TestItem.AddressB, _account1);
            tree.Commit(1);
            Account a1_0 = tree.Get(TestItem.AddressB, root0);
            Assert.IsNull(a1_0);

            tree.Set(TestItem.AddressB, _account2);
            tree.Commit(2);

            a1_0 = tree.Get(TestItem.AddressB, root0);

            Assert.IsNotNull(a1_0);
        }

        [Test]
        public void History_delete_when_max_number_blocks_exceeded()
        {
            MemDb db = new();
            StateTreeByPath tree = new(new TrieStoreByPath(db, No.Pruning, Persist.EveryBlock, LimboLogs.Instance, 5), LimboLogs.Instance);
            tree.Set(TestItem.AddressA, _account0);
            tree.Commit(0);
            Keccak root0 = tree.RootHash;
            Keccak root2 = null;

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
            Assert.AreEqual((UInt256)(2 * 5), a1_2.Balance);
        }

        [Test]
        public void CopyStateTest()
        {
            MemDb memDb = new MemDb();
            using TrieStoreByPath trieStore = new TrieStoreByPath(memDb, No.Pruning, Persist.EveryBlock, LimboLogs.Instance);

            StateTreeByPath tree = new(trieStore, LimboLogs.Instance);

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
    }
}
