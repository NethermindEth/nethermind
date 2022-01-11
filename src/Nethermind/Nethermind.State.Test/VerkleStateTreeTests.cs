//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 
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
    [TestFixture]
    public class VerkleStateTreeTests
    {
        private readonly Account _account0 = Build.An.Account.WithBalance(0).TestObject;
        private readonly Account _account1 = Build.An.Account.WithBalance(1).TestObject;
        private readonly Account _account2 = Build.An.Account.WithBalance(2).TestObject;
        private readonly Account _account3 = Build.An.Account.WithBalance(3).TestObject;
        private readonly byte[] treeKeyVersion =
        {
            121, 85, 7, 198, 131, 230, 143, 90, 165, 129, 173, 81, 186, 89, 19, 191, 13, 107, 197, 120, 243, 229,
            224, 183, 72, 25, 6, 8, 210, 159, 31, 0
        };

        private readonly byte[] treeKeyBalance =
        {
            121, 85, 7, 198, 131, 230, 143, 90, 165, 129, 173, 81, 186, 89, 19, 191, 13, 107, 197, 120, 243, 229,
            224, 183, 72, 25, 6, 8, 210, 159, 31, 1
        };
        
        private readonly byte[] treeKeyNonce =
        {
            121, 85, 7, 198, 131, 230, 143, 90, 165, 129, 173, 81, 186, 89, 19, 191, 13, 107, 197, 120, 243, 229,
            224, 183, 72, 25, 6, 8, 210, 159, 31, 2
        };
        
        private readonly byte[] treeKeyCodeKeccak =
        {
            121, 85, 7, 198, 131, 230, 143, 90, 165, 129, 173, 81, 186, 89, 19, 191, 13, 107, 197, 120, 243, 229,
            224, 183, 72, 25, 6, 8, 210, 159, 31, 3
        };

        private readonly byte[] treeKeyCodeSize =
        {
            121, 85, 7, 198, 131, 230, 143, 90, 165, 129, 173, 81, 186, 89, 19, 191, 13, 107, 197, 120, 243, 229,
            224, 183, 72, 25, 6, 8, 210, 159, 31, 4
        };
        
        [SetUp]
        public void Setup()
        {
            Trie.Metrics.TreeNodeHashCalculations = 0;
            Trie.Metrics.TreeNodeRlpDecodings = 0;
            Trie.Metrics.TreeNodeRlpEncodings = 0;
        }
        
        
        [Test]
        public void Get_Account_Keys()
        {
            VerkleStateTree tree = new(LimboLogs.Instance);
            byte[][] treeKeys = tree.GetTreeKeysForAccount(TestItem.AddressA);
            Assert.AreEqual(treeKeys.Length, 5);
            Assert.AreEqual(treeKeys[AccountTreeIndexes.Version], treeKeyVersion);
            Assert.AreEqual(treeKeys[AccountTreeIndexes.Balance], treeKeyBalance);
            Assert.AreEqual(treeKeys[AccountTreeIndexes.Nonce], treeKeyNonce);
            Assert.AreEqual(treeKeys[AccountTreeIndexes.CodeHash], treeKeyCodeKeccak);
            Assert.AreEqual(treeKeys[AccountTreeIndexes.CodeSize], treeKeyCodeSize);
        }
        
        [Test]
        public void Set_Get_Keys()
        {
            VerkleStateTree tree = new(LimboLogs.Instance);
            byte[][] treeKeys = tree.GetTreeKeysForAccount(TestItem.AddressA);
            
            byte[] value =  {
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1
            };
            
            tree.SetValue(treeKeys[AccountTreeIndexes.Version], value);
            tree.SetValue(treeKeys[AccountTreeIndexes.Balance], value);
            tree.SetValue(treeKeys[AccountTreeIndexes.Nonce], value);
            tree.SetValue(treeKeys[AccountTreeIndexes.CodeHash], value);
            tree.SetValue(treeKeys[AccountTreeIndexes.CodeSize], value);

            tree.GetValue(treeKeys[AccountTreeIndexes.Version]);
            tree.GetValue(treeKeys[AccountTreeIndexes.Balance]);
            tree.GetValue(treeKeys[AccountTreeIndexes.Nonce]);
            tree.GetValue(treeKeys[AccountTreeIndexes.CodeHash]);
            tree.GetValue(treeKeys[AccountTreeIndexes.CodeSize]);

        }
        
        [Test]
        public void Set_Account_Value_Keys()
        {
            VerkleStateTree tree = new(LimboLogs.Instance);
            byte[][] treeKeys = tree.GetTreeKeysForAccount(TestItem.AddressA);
            byte[] version = {
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1
            };
            byte[] balance = {
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2
            };
            byte[] nonce = {
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0
            };
            byte[] codeHash = {
                197, 210, 70, 1, 134, 247, 35, 60, 146, 126, 125, 178, 220, 199, 3, 192, 229, 0, 182, 83, 202, 130, 39,
                59, 123, 250, 216, 4, 93, 133, 164, 112
            };
            byte[] codeSize = {
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1
            };
            
            tree.SetValue(treeKeys[AccountTreeIndexes.Version], version);
            tree.SetValue(treeKeys[AccountTreeIndexes.Balance], balance);
            tree.SetValue(treeKeys[AccountTreeIndexes.Nonce], nonce);
            tree.SetValue(treeKeys[AccountTreeIndexes.CodeHash], codeHash);
            tree.SetValue(treeKeys[AccountTreeIndexes.CodeSize], codeSize);

            tree.GetValue(treeKeys[AccountTreeIndexes.Version]);
            tree.GetValue(treeKeys[AccountTreeIndexes.Balance]);
            tree.GetValue(treeKeys[AccountTreeIndexes.Nonce]);
            tree.GetValue(treeKeys[AccountTreeIndexes.CodeHash]);
            tree.GetValue(treeKeys[AccountTreeIndexes.CodeSize]);


        }
        
        [Test]
        public void Set_Account_Data_Type_Keys()
        {
            VerkleStateTree tree = new(LimboLogs.Instance);
            byte[][] treeKeys = tree.GetTreeKeysForAccount(TestItem.AddressA);
            UInt256 version = UInt256.Zero;
            UInt256 balance = new (2);
            UInt256 nonce = UInt256.Zero;
            Keccak codeHash = Keccak.OfAnEmptyString;
            UInt256 codeSize = UInt256.Zero;
            
            tree.SetValue(treeKeys[AccountTreeIndexes.Version], version.ToBigEndian());
            tree.SetValue(treeKeys[AccountTreeIndexes.Balance], balance.ToBigEndian());
            tree.SetValue(treeKeys[AccountTreeIndexes.Nonce], nonce.ToBigEndian());
            tree.SetValue(treeKeys[AccountTreeIndexes.CodeHash], codeHash.Bytes);
            tree.SetValue(treeKeys[AccountTreeIndexes.CodeSize], codeSize.ToBigEndian());

            tree.GetValue(treeKeys[AccountTreeIndexes.Version]);
            tree.GetValue(treeKeys[AccountTreeIndexes.Balance]);
            tree.GetValue(treeKeys[AccountTreeIndexes.Nonce]);
            tree.GetValue(treeKeys[AccountTreeIndexes.CodeHash]);
            tree.GetValue(treeKeys[AccountTreeIndexes.CodeSize]);


        }
        
        [Test]
        public void Set_Account_Keys()
        {
            VerkleStateTree tree = new(LimboLogs.Instance);
            byte[][] treeKeys = tree.GetTreeKeysForAccount(TestItem.AddressA);
            byte[] version = _account2.Version.ToBigEndian();
            byte[] balance = _account2.Balance.ToBigEndian();
            byte[] nonce = _account2.Nonce.ToBigEndian();
            byte[] codeHash = _account2.CodeHash.Bytes;
            byte[] codeSize = _account2.CodeSize.ToBigEndian();
            
            tree.SetValue(treeKeys[AccountTreeIndexes.Version], version);
            tree.SetValue(treeKeys[AccountTreeIndexes.Balance], balance);
            tree.SetValue(treeKeys[AccountTreeIndexes.Nonce], nonce);
            tree.SetValue(treeKeys[AccountTreeIndexes.CodeHash], codeHash);
            tree.SetValue(treeKeys[AccountTreeIndexes.CodeSize], codeSize);
            
            tree.GetValue(treeKeys[AccountTreeIndexes.Version]);
            tree.GetValue(treeKeys[AccountTreeIndexes.Balance]);
            tree.GetValue(treeKeys[AccountTreeIndexes.Nonce]);
            tree.GetValue(treeKeys[AccountTreeIndexes.CodeHash]);
            tree.GetValue(treeKeys[AccountTreeIndexes.CodeSize]);


        }

       
        
        // public void Set_And_Get_Account()
        // {
        //     VerkleStateTree tree = new(LimboLogs.Instance);
        //     tree.Set(TestItem.AddressA, _account2);
        //     Account accountA = tree.Get(TestItem.AddressA);
        //     // tree.Commit(0);
        //     // Assert.AreEqual(0, db.ReadsCount, "reads");
        // }
        
        // [Test]
        // public void Minimal_writes_when_setting_on_empty()
        // {
        //     VerkleStateTree tree = new(LimboLogs.Instance);
        //     tree.Set(TestItem.AddressA, _account0);
        //     tree.Set(TestItem.AddressB, _account0);
        //     tree.Set(TestItem.AddressC, _account0);
        //     // tree.Commit(0);
        //     Assert.AreEqual(5, db.WritesCount, "writes"); // branch, branch, two leaves (one is stored as RLP)
        // }
        //
        // [Test]
        // public void Minimal_writes_when_setting_on_empty_scenario_2()
        // {
        //     MemDb db = new();
        //     StateTree tree = new(new TrieStore(db, LimboLogs.Instance), LimboLogs.Instance);
        //     tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), _account0);
        //     tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb0"), _account0);
        //     tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb1"), _account0);
        //     tree.Commit(0);
        //     Assert.AreEqual(7, db.WritesCount, "writes"); // extension, branch, leaf, extension, branch, 2x same leaf
        //     Assert.AreEqual(7, Trie.Metrics.TreeNodeHashCalculations, "hashes");
        //     Assert.AreEqual(7, Trie.Metrics.TreeNodeRlpEncodings, "encodings");
        // }
        //
        // [Test]
        // public void Minimal_writes_when_setting_on_empty_scenario_3()
        // {
        //     MemDb db = new();
        //     StateTree tree = new(new TrieStore(db, LimboLogs.Instance), LimboLogs.Instance);
        //     tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), _account0);
        //     tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb0"), _account0);
        //     tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb1"), _account0);
        //     tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb1"), null);
        //     tree.Commit(0);
        //     Assert.AreEqual(4, db.WritesCount, "writes"); // extension, branch, 2x leaf
        //     Assert.AreEqual(4, Trie.Metrics.TreeNodeHashCalculations, "hashes");
        //     Assert.AreEqual(4, Trie.Metrics.TreeNodeRlpEncodings, "encodings");
        // }
        //
        // [Test]
        // public void Minimal_writes_when_setting_on_empty_scenario_4()
        // {
        //     MemDb db = new();
        //     StateTree tree = new(new TrieStore(db, LimboLogs.Instance), LimboLogs.Instance);
        //     tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), _account0);
        //     tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb0"), _account0);
        //     tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb1"), _account0);
        //     tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb0"), null);
        //     tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb1"), null);
        //     tree.Commit(0);
        //     Assert.AreEqual(1, db.WritesCount, "writes"); // extension, branch, 2x leaf
        //     Assert.AreEqual(1, Trie.Metrics.TreeNodeHashCalculations, "hashes");
        //     Assert.AreEqual(1, Trie.Metrics.TreeNodeRlpEncodings, "encodings");
        // }
        //
        // [Test]
        // public void Minimal_writes_when_setting_on_empty_scenario_5()
        // {
        //     MemDb db = new();
        //     StateTree tree = new(new TrieStore(db, LimboLogs.Instance), LimboLogs.Instance);
        //     tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), _account0);
        //     tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb0"), _account0);
        //     tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb1"), _account0);
        //     tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb0"), null);
        //     tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb1"), null);
        //     tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), null);
        //     tree.Commit(0);
        //     Assert.AreEqual(0, db.WritesCount, "writes"); // extension, branch, 2x leaf
        //     Assert.AreEqual(0, Trie.Metrics.TreeNodeHashCalculations, "hashes");
        //     Assert.AreEqual(0, Trie.Metrics.TreeNodeRlpEncodings, "encodings");
        // }
        //
        // [Test]
        // public void Scenario_traverse_extension_read_full_match()
        // {
        //     MemDb db = new();
        //     StateTree tree = new(new TrieStore(db, LimboLogs.Instance), LimboLogs.Instance);
        //     tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), _account0);
        //     tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111111"), _account1);
        //     Account account = tree.Get(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111111"));
        //     Assert.AreEqual(0, db.ReadsCount);
        //     Assert.AreEqual(_account1.Balance, account.Balance);
        //     tree.UpdateRootHash();
        //     Keccak rootHash = tree.RootHash;
        //     Assert.AreEqual("0xf99f1d3234bad8d63d818db36ff63eefc8916263e654db8b800d3bd03f6339a5", rootHash.ToString(true));
        //     tree.Commit(0);
        //     Assert.AreEqual("0xf99f1d3234bad8d63d818db36ff63eefc8916263e654db8b800d3bd03f6339a5", rootHash.ToString(true));
        // }
        //
        // [Test]
        // public void Scenario_traverse_extension_read_missing()
        // {
        //     MemDb db = new();
        //     StateTree tree = new(new TrieStore(db, LimboLogs.Instance), LimboLogs.Instance);
        //     tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), _account0);
        //     tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111111"), _account1);
        //     Account account = tree.Get(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeedddddddddddddddddddddddd"));
        //     Assert.Null(account);
        //     tree.UpdateRootHash();
        //     Keccak rootHash = tree.RootHash;
        //     Assert.AreEqual("0xf99f1d3234bad8d63d818db36ff63eefc8916263e654db8b800d3bd03f6339a5", rootHash.ToString(true));
        //     tree.Commit(0);
        //     Assert.AreEqual("0xf99f1d3234bad8d63d818db36ff63eefc8916263e654db8b800d3bd03f6339a5", rootHash.ToString(true));
        // }
        //
        // [Test]
        // public void Scenario_traverse_extension_new_branching()
        // {
        //     MemDb db = new();
        //     StateTree tree = new(new TrieStore(db, LimboLogs.Instance), LimboLogs.Instance);
        //     tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), _account0);
        //     tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111111"), _account1);
        //     tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeedddddddddddddddddddddddd"), _account2);
        //     tree.UpdateRootHash();
        //     Keccak rootHash = tree.RootHash;
        //     Assert.AreEqual("0x543c960143a2a06b685d6b92f0c37000273e616bc23888521e7edf15ad06da46", rootHash.ToString(true));
        //     tree.Commit(0);
        //     Assert.AreEqual("0x543c960143a2a06b685d6b92f0c37000273e616bc23888521e7edf15ad06da46", rootHash.ToString(true));
        // }
        //
        // [Test]
        // public void Scenario_traverse_extension_delete_missing()
        // {
        //     MemDb db = new();
        //     StateTree tree = new(new TrieStore(db, LimboLogs.Instance), LimboLogs.Instance);
        //     tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), _account0);
        //     tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111111"), _account1);
        //     tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeddddddddddddddddddddddddd"), null);
        //     Assert.AreEqual(0, db.ReadsCount);
        //     tree.UpdateRootHash();
        //     Keccak rootHash = tree.RootHash;
        //     Assert.AreEqual("0xf99f1d3234bad8d63d818db36ff63eefc8916263e654db8b800d3bd03f6339a5", rootHash.ToString(true));
        //     tree.Commit(0);
        //     Assert.AreEqual("0xf99f1d3234bad8d63d818db36ff63eefc8916263e654db8b800d3bd03f6339a5", rootHash.ToString(true));
        // }
        //
        // [Test]
        // public void Scenario_traverse_extension_create_new_extension()
        // {
        //     MemDb db = new();
        //     StateTree tree = new(new TrieStore(db, LimboLogs.Instance), LimboLogs.Instance);
        //     tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), _account0);
        //     tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111111"), _account1);
        //     tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeaaaaaaaaaaaaaaaab00000000"), _account2);
        //     tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeaaaaaaaaaaaaaaaab11111111"), _account3);
        //     Assert.AreEqual(0, db.ReadsCount);
        //     tree.UpdateRootHash();
        //     Keccak rootHash = tree.RootHash;
        //     Assert.AreEqual("0x0918112fc898173562441709a2c1cbedb80d1aaecaeadf2f3e9492eeaa568c67", rootHash.ToString(true));
        //     tree.Commit(0);
        //     Assert.AreEqual("0x0918112fc898173562441709a2c1cbedb80d1aaecaeadf2f3e9492eeaa568c67", rootHash.ToString(true));
        // }
        //
        // [Test]
        // public void Scenario_traverse_leaf_update_new_value()
        // {
        //     MemDb db = new();
        //     StateTree tree = new(new TrieStore(db, LimboLogs.Instance), LimboLogs.Instance);
        //     tree.Set(new Keccak("1111111111111111111111111111111111111111111111111111111111111111"), _account0);
        //     tree.Set(new Keccak("1111111111111111111111111111111111111111111111111111111111111111"), _account1);
        //     tree.UpdateRootHash();
        //     Keccak rootHash = tree.RootHash;
        //     Assert.AreEqual("0xaa5c248d4b4b8c27a654296a8e0cc51131eb9011d9166fa0fca56a966489e169", rootHash.ToString(true));
        //     tree.Commit(0);
        //     Assert.AreEqual("0xaa5c248d4b4b8c27a654296a8e0cc51131eb9011d9166fa0fca56a966489e169", rootHash.ToString(true));
        // }
        //
        // [Test]
        // public void Scenario_traverse_leaf_update_no_change()
        // {
        //     MemDb db = new();
        //     StateTree tree = new(new TrieStore(db, LimboLogs.Instance), LimboLogs.Instance);
        //     tree.Set(new Keccak("1111111111111111111111111111111111111111111111111111111111111111"), _account0);
        //     tree.Set(new Keccak("1111111111111111111111111111111111111111111111111111111111111111"), _account0);
        //     tree.UpdateRootHash();
        //     Keccak rootHash = tree.RootHash;
        //     Assert.AreEqual("0x491fbb33aaff22c0a7ff68d5c81ec114dddf89d022ccdee838a0e9d6cd45cab4", rootHash.ToString(true));
        //     tree.Commit(0);
        //     Assert.AreEqual("0x491fbb33aaff22c0a7ff68d5c81ec114dddf89d022ccdee838a0e9d6cd45cab4", rootHash.ToString(true));
        // }
        //
        // [Test]
        // public void Scenario_traverse_leaf_read_matching_leaf()
        // {
        //     MemDb db = new();
        //     StateTree tree = new(new TrieStore(db, LimboLogs.Instance), LimboLogs.Instance);
        //     tree.Set(new Keccak("1111111111111111111111111111111111111111111111111111111111111111"), _account0);
        //     tree.Set(new Keccak("1111111111111111111111111111111111111111111111111111111111111111"), null);
        //     tree.UpdateRootHash();
        //     Keccak rootHash = tree.RootHash;
        //     Assert.AreEqual("0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421", rootHash.ToString(true));
        //     tree.Commit(0);
        //     Assert.AreEqual("0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421", rootHash.ToString(true));
        // }
        //
        // [Test]
        // public void Scenario_traverse_leaf_delete_missing()
        // {
        //     MemDb db = new();
        //     StateTree tree = new(new TrieStore(db, LimboLogs.Instance), LimboLogs.Instance);
        //     tree.Set(new Keccak("1111111111111111111111111111111111111111111111111111111111111111"), _account0);
        //     tree.Set(new Keccak("1111111111111111111111111111111ddddddddddddddddddddddddddddddddd"), null);
        //     tree.UpdateRootHash();
        //     Keccak rootHash = tree.RootHash;
        //     Assert.AreEqual("0x491fbb33aaff22c0a7ff68d5c81ec114dddf89d022ccdee838a0e9d6cd45cab4", rootHash.ToString(true));
        //     tree.Commit(0);
        //     Assert.AreEqual("0x491fbb33aaff22c0a7ff68d5c81ec114dddf89d022ccdee838a0e9d6cd45cab4", rootHash.ToString(true));
        // }
        //
        // [Test]
        // public void Scenario_traverse_leaf_update_with_extension()
        // {
        //     MemDb db = new();
        //     StateTree tree = new(new TrieStore(db, LimboLogs.Instance), LimboLogs.Instance);
        //     tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111111111111111111111111111111"), _account0);
        //     tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000000000000000000000000000"), _account1);
        //     tree.UpdateRootHash();
        //     Keccak rootHash = tree.RootHash;
        //     Assert.AreEqual("0x215a4bab4cf2d5ebbaa59c82ae94c9707fcf4cc0ca1fe7e18f918e46db428ef9", rootHash.ToString(true));
        //     tree.Commit(0);
        //     Assert.AreEqual("0x215a4bab4cf2d5ebbaa59c82ae94c9707fcf4cc0ca1fe7e18f918e46db428ef9", rootHash.ToString(true));
        // }
        //
        // [Test]
        // public void Scenario_traverse_leaf_delete_matching_leaf()
        // {
        //     MemDb db = new();
        //     StateTree tree = new(new TrieStore(db, LimboLogs.Instance), LimboLogs.Instance);
        //     tree.Set(new Keccak("1111111111111111111111111111111111111111111111111111111111111111"), _account0);
        //     Account account = tree.Get(new Keccak("1111111111111111111111111111111111111111111111111111111111111111"));
        //     Assert.NotNull(account);
        //     tree.UpdateRootHash();
        //     Keccak rootHash = tree.RootHash;
        //     Assert.AreEqual("0x491fbb33aaff22c0a7ff68d5c81ec114dddf89d022ccdee838a0e9d6cd45cab4", rootHash.ToString(true));
        //     tree.Commit(0);
        //     Assert.AreEqual("0x491fbb33aaff22c0a7ff68d5c81ec114dddf89d022ccdee838a0e9d6cd45cab4", rootHash.ToString(true));
        // }
        //
        // [Test]
        // public void Scenario_traverse_leaf_read_missing()
        // {
        //     MemDb db = new();
        //     StateTree tree = new(new TrieStore(db, LimboLogs.Instance), LimboLogs.Instance);
        //     tree.Set(new Keccak("1111111111111111111111111111111111111111111111111111111111111111"), _account0);
        //     Account account = tree.Get(new Keccak("111111111111111111111111111111111111111111111111111111111ddddddd"));
        //     Assert.Null(account);
        //     tree.UpdateRootHash();
        //     Keccak rootHash = tree.RootHash;
        //     Assert.AreEqual("0x491fbb33aaff22c0a7ff68d5c81ec114dddf89d022ccdee838a0e9d6cd45cab4", rootHash.ToString(true));
        //     tree.Commit(0);
        //     Assert.AreEqual("0x491fbb33aaff22c0a7ff68d5c81ec114dddf89d022ccdee838a0e9d6cd45cab4", rootHash.ToString(true));
        // }
        //
        // [Test]
        // public void Scenario_traverse_branch_update_missing()
        // {
        //     MemDb db = new();
        //     StateTree tree = new(new TrieStore(db, LimboLogs.Instance), LimboLogs.Instance);
        //     tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000"), _account0);
        //     tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111"), _account1);
        //     tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb22222"), _account2);
        //     tree.UpdateRootHash();
        //     Keccak rootHash = tree.RootHash;
        //     Assert.AreEqual("0xc063af0bd3dd88320bc852ff8452049c42fbc06d1a69661567bd427572824cbf", rootHash.ToString(true));
        //     tree.Commit(0);
        //     Assert.AreEqual("0xc063af0bd3dd88320bc852ff8452049c42fbc06d1a69661567bd427572824cbf", rootHash.ToString(true));
        // }
        //
        // [Test]
        // public void Scenario_traverse_branch_read_missing()
        // {
        //     MemDb db = new();
        //     StateTree tree = new(new TrieStore(db, LimboLogs.Instance), LimboLogs.Instance);
        //     tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000"), _account0);
        //     tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111"), _account1);
        //     Account account = tree.Get(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb22222"));
        //     Assert.Null(account);
        //     tree.UpdateRootHash();
        //     Keccak rootHash = tree.RootHash;
        //     Assert.AreEqual("0x94a193704e99c219d9a21428eb37d6d2d71b3d2cea80c77ff0e201c0df70a283", rootHash.ToString(true));
        //     tree.Commit(0);
        //     Assert.AreEqual("0x94a193704e99c219d9a21428eb37d6d2d71b3d2cea80c77ff0e201c0df70a283", rootHash.ToString(true));
        // }
        //
        // [Test]
        // public void Scenario_traverse_branch_delete_missing()
        // {
        //     MemDb db = new();
        //     StateTree tree = new(new TrieStore(db, LimboLogs.Instance), LimboLogs.Instance);
        //     tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000"), _account0);
        //     tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111"), _account1);
        //     tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb22222"), null);
        //     tree.UpdateRootHash();
        //     Keccak rootHash = tree.RootHash;
        //     Assert.AreEqual("0x94a193704e99c219d9a21428eb37d6d2d71b3d2cea80c77ff0e201c0df70a283", rootHash.ToString(true));
        //     tree.Commit(0);
        //     Assert.AreEqual("0x94a193704e99c219d9a21428eb37d6d2d71b3d2cea80c77ff0e201c0df70a283", rootHash.ToString(true));
        // }
        //
        // [Test]
        // public void Minimal_hashes_when_setting_on_empty()
        // {
        //     MemDb db = new();
        //     StateTree tree = new(new TrieStore(db, LimboLogs.Instance), LimboLogs.Instance);
        //     tree.Set(TestItem.AddressA, _account0);
        //     tree.Set(TestItem.AddressB, _account0);
        //     tree.Set(TestItem.AddressC, _account0);
        //     tree.Commit(0);
        //     Assert.AreEqual(5, Trie.Metrics.TreeNodeHashCalculations, "hashes"); // branch, branch, three leaves
        // }
        //
        // [Test]
        // public void Minimal_encodings_when_setting_on_empty()
        // {
        //     MemDb db = new();
        //     StateTree tree = new(new TrieStore(db, LimboLogs.Instance), LimboLogs.Instance);
        //     tree.Set(TestItem.AddressA, _account0);
        //     tree.Set(TestItem.AddressB, _account0);
        //     tree.Set(TestItem.AddressC, _account0);
        //     tree.Commit(0);
        //     Assert.AreEqual(5, Trie.Metrics.TreeNodeRlpEncodings, "encodings"); // branch, branch, three leaves
        // }
        //
        // [Test]
        // public void Zero_decodings_when_setting_on_empty()
        // {
        //     MemDb db = new();
        //     StateTree tree = new(new TrieStore(db, LimboLogs.Instance), LimboLogs.Instance);
        //     tree.Set(TestItem.AddressA, _account0);
        //     tree.Set(TestItem.AddressB, _account0);
        //     tree.Set(TestItem.AddressC, _account0);
        //     tree.Commit(0);
        //     Assert.AreEqual(0, Trie.Metrics.TreeNodeRlpDecodings, "decodings");
        // }
        //
        // [Test]
        // public void No_writes_on_continues_update()
        // {
        //     MemDb db = new();
        //     StateTree tree = new(new TrieStore(db, LimboLogs.Instance), LimboLogs.Instance);
        //     tree.Set(TestItem.AddressA, _account0);
        //     tree.Set(TestItem.AddressA, _account1);
        //     tree.Set(TestItem.AddressA, _account2);
        //     tree.Set(TestItem.AddressA, _account3);
        //     tree.Commit(0);
        //     Assert.AreEqual(1, db.WritesCount, "writes"); // extension, branch, two leaves
        // }
        //
        // [Ignore("This is not critical")]
        // [Test]
        // public void No_writes_on_reverted_update()
        // {
        //     MemDb db = new();
        //     StateTree tree = new(new TrieStore(db, LimboLogs.Instance), LimboLogs.Instance);
        //     tree.Set(TestItem.AddressA, _account0);
        //     tree.Commit(0);
        //     Assert.AreEqual(1, db.WritesCount, "writes before"); // extension, branch, two leaves
        //     tree.Set(TestItem.AddressA, _account1);
        //     tree.Set(TestItem.AddressA, _account0);
        //     tree.Commit(0);
        //     Assert.AreEqual(1, db.WritesCount, "writes after"); // extension, branch, two leaves
        // }
        //
        // [Test]
        // public void No_writes_without_commit()
        // {
        //     MemDb db = new();
        //     StateTree tree = new(new TrieStore(db, LimboLogs.Instance), LimboLogs.Instance);
        //     tree.Set(TestItem.AddressA, _account0);
        //     Assert.AreEqual(0, db.WritesCount, "writes");
        // }
        //
        // [Test]
        // public void Can_ask_about_root_hash_without_commiting()
        // {
        //     MemDb db = new();
        //     StateTree tree = new(new TrieStore(db, LimboLogs.Instance), LimboLogs.Instance);
        //     tree.Set(TestItem.AddressA, _account0);
        //     tree.UpdateRootHash();
        //     Assert.AreEqual("0x545a417202afcb10925b2afddb70a698710bb1cf4ab32942c42e9f019d564fdc", tree.RootHash.ToString(true));
        // }
        //
        // [Test]
        // public void Can_ask_about_root_hash_without_when_emptied()
        // {
        //     MemDb db = new();
        //     StateTree tree = new(new TrieStore(db, LimboLogs.Instance), LimboLogs.Instance);
        //     tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), _account0);
        //     tree.UpdateRootHash();
        //     Assert.AreNotEqual(PatriciaTree.EmptyTreeHash, tree.RootHash);
        //     tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb0"), _account0);
        //     tree.UpdateRootHash();
        //     Assert.AreNotEqual(PatriciaTree.EmptyTreeHash, tree.RootHash);
        //     tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb1"), _account0);
        //     tree.UpdateRootHash();
        //     Assert.AreNotEqual(PatriciaTree.EmptyTreeHash, tree.RootHash);
        //     tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb0"), null);
        //     tree.UpdateRootHash();
        //     Assert.AreNotEqual(PatriciaTree.EmptyTreeHash, tree.RootHash);
        //     tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb1"), null);
        //     tree.UpdateRootHash();
        //     Assert.AreNotEqual(PatriciaTree.EmptyTreeHash, tree.RootHash);
        //     tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), null);
        //     tree.UpdateRootHash();
        //     Assert.AreEqual(PatriciaTree.EmptyTreeHash, tree.RootHash);
        //     tree.Commit(0);
        //     Assert.AreEqual(PatriciaTree.EmptyTreeHash, tree.RootHash);
        // }
        //
        // [Test]
        // public void hash_empty_tree_root_hash_initially()
        // {
        //     MemDb db = new();
        //     StateTree tree = new(new TrieStore(db, LimboLogs.Instance), LimboLogs.Instance);
        //     Assert.AreEqual(PatriciaTree.EmptyTreeHash, tree.RootHash);
        // }
        //
        // [Test]
        // public void Can_save_null()
        // {
        //     MemDb db = new();
        //     StateTree tree = new(new TrieStore(db, LimboLogs.Instance), LimboLogs.Instance);
        //     tree.Set(TestItem.AddressA, null);
        // }
    }
}

