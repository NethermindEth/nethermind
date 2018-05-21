using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.Store.Test
{
    [TestFixture]
    public class StateTreeTests
    {
        private readonly Account _account0 = Build.An.Account.WithBalance(0).TestObject;
        private readonly Account _account1 = Build.An.Account.WithBalance(1).TestObject;
        private readonly Account _account2 = Build.An.Account.WithBalance(2).TestObject;
        private readonly Account _account3 = Build.An.Account.WithBalance(3).TestObject;

        [SetUp]
        public void Setup()
        {
            Metrics.TreeNodeHashCalculations = 0;
            Metrics.TreeNodeRlpDecodings = 0;
            Metrics.TreeNodeRlpEncodings = 0;
        }
        
        [Test]
        public void No_reads_when_setting_on_empty()
        {
            MemDb db = new MemDb();
            StateTree tree = new StateTree(db);
            tree.Set(TestObject.AddressA, _account0);
            tree.Set(TestObject.AddressB, _account0);
            tree.Set(TestObject.AddressC, _account0);
            tree.Commit();
            Assert.AreEqual(0, db.ReadsCount, "reads");
        }
        
        [Test]
        public void Minimal_writes_when_setting_on_empty()
        {
            MemDb db = new MemDb();
            StateTree tree = new StateTree(db);
            tree.Set(TestObject.AddressA, _account0);
            tree.Set(TestObject.AddressB, _account0);
            tree.Set(TestObject.AddressC, _account0);
            tree.Commit();
            Assert.AreEqual(5, db.WritesCount, "writes"); // branch, branch, two leaves (one is stored as RLP)
        }
        
        [Test]
        public void Minimal_writes_when_setting_on_empty_scenario_2()
        {
            MemDb db = new MemDb();
            StateTree tree = new StateTree(db);
            tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), _account0);
            tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb0"), _account0);
            tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb1"), _account0);
            tree.Commit();
            Assert.AreEqual(7, db.WritesCount, "writes"); // extension, branch, leaf, extension, branch, leaf, leaf
            Assert.AreEqual(7, Metrics.TreeNodeHashCalculations, "hashes");
            Assert.AreEqual(7, Metrics.TreeNodeRlpEncodings, "encodings");
        }
        
        [Test]
        public void Minimal_writes_when_setting_on_empty_scenario_3()
        {
            MemDb db = new MemDb();
            StateTree tree = new StateTree(db);
            tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), _account0);
            tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb0"), _account0);
            tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb1"), _account0);
            tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb1"), null);
            tree.Commit();
            Assert.AreEqual(4, db.WritesCount, "writes"); // extension, branch, 2x leaf
            Assert.AreEqual(4, Metrics.TreeNodeHashCalculations, "hashes");
            Assert.AreEqual(4, Metrics.TreeNodeRlpEncodings, "encodings");
        }
        
        [Test]
        public void Minimal_writes_when_setting_on_empty_scenario_4()
        {
            MemDb db = new MemDb();
            StateTree tree = new StateTree(db);
            tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), _account0);
            tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb0"), _account0);
            tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb1"), _account0);
            tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb0"), null);
            tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb1"), null);
            tree.Commit();
            Assert.AreEqual(1, db.WritesCount, "writes"); // extension, branch, 2x leaf
            Assert.AreEqual(1, Metrics.TreeNodeHashCalculations, "hashes");
            Assert.AreEqual(1, Metrics.TreeNodeRlpEncodings, "encodings");
        }
        
        [Test]
        public void Minimal_writes_when_setting_on_empty_scenario_5()
        {
            MemDb db = new MemDb();
            StateTree tree = new StateTree(db);
            tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), _account0);
            tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb0"), _account0);
            tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb1"), _account0);
            tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb0"), null);
            tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb1eeeeeb1"), null);
            tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), null);
            tree.Commit();
            Assert.AreEqual(0, db.WritesCount, "writes"); // extension, branch, 2x leaf
            Assert.AreEqual(0, Metrics.TreeNodeHashCalculations, "hashes");
            Assert.AreEqual(0, Metrics.TreeNodeRlpEncodings, "encodings");
        }
        
        [Test]
        public void Scenario_traverse_extension_read_full_match()
        {
            MemDb db = new MemDb();
            StateTree tree = new StateTree(db);
            tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), _account0);
            tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111111"), _account1);
            Account account = tree.Get(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111111"));
            Assert.AreEqual(0, db.ReadsCount);
            Assert.AreEqual(_account1.Balance, account.Balance);
            tree.UpdateRootHash();
            tree.Commit();
        }
        
        [Test]
        public void Scenario_traverse_extension_read_missing()
        {
            MemDb db = new MemDb();
            StateTree tree = new StateTree(db);
            tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), _account0);
            tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111111"), _account1);
            Account account = tree.Get(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeedddddddddddddddddddddddd"));
            Assert.Null(account);
            tree.UpdateRootHash();
            tree.Commit();
        }
        
        [Test]
        public void Scenario_traverse_extension_new_branching()
        {
            MemDb db = new MemDb();
            StateTree tree = new StateTree(db);
            tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), _account0);
            tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111111"), _account1);
            tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeedddddddddddddddddddddddd"), _account2);
            tree.UpdateRootHash();
            tree.Commit();
        }
        
        [Test]
        public void Scenario_traverse_extension_delete_missing()
        {
            MemDb db = new MemDb();
            StateTree tree = new StateTree(db);
            tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), _account0);
            tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111111"), _account1);
            tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeddddddddddddddddddddddddd"), null);
            Assert.AreEqual(0, db.ReadsCount);
            tree.UpdateRootHash();
            tree.Commit();
        }
        
        [Test]
        public void Scenario_traverse_extension_create_new_extension()
        {
            MemDb db = new MemDb();
            StateTree tree = new StateTree(db);
            tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000"), _account0);
            tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111111"), _account1);
            tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeaaaaaaaaaaaaaaaab00000000"), _account2);
            tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeaaaaaaaaaaaaaaaab11111111"), _account3);
            Assert.AreEqual(0, db.ReadsCount);
            tree.UpdateRootHash();
            tree.Commit();
        }
       
        [Test]
        public void Scenario_traverse_leaf_update_new_value()
        {
            MemDb db = new MemDb();
            StateTree tree = new StateTree(db);
            tree.Set(new Keccak("1111111111111111111111111111111111111111111111111111111111111111"), _account0);
            tree.Set(new Keccak("1111111111111111111111111111111111111111111111111111111111111111"), _account1);
            tree.UpdateRootHash();
            tree.Commit();
        }
        
        [Test]
        public void Scenario_traverse_leaf_update_no_change()
        {
            MemDb db = new MemDb();
            StateTree tree = new StateTree(db);
            tree.Set(new Keccak("1111111111111111111111111111111111111111111111111111111111111111"), _account0);
            tree.Set(new Keccak("1111111111111111111111111111111111111111111111111111111111111111"), _account0);
            tree.UpdateRootHash();
            tree.Commit();
        }
        
        [Test]
        public void Scenario_traverse_leaf_read_matching_leaf()
        {
            MemDb db = new MemDb();
            StateTree tree = new StateTree(db);
            tree.Set(new Keccak("1111111111111111111111111111111111111111111111111111111111111111"), _account0);
            tree.Set(new Keccak("1111111111111111111111111111111111111111111111111111111111111111"), null);
            tree.UpdateRootHash();
            tree.Commit();
        }
        
        [Test]
        public void Scenario_traverse_leaf_delete_missing()
        {
            MemDb db = new MemDb();
            StateTree tree = new StateTree(db);
            tree.Set(new Keccak("1111111111111111111111111111111111111111111111111111111111111111"), _account0);
            tree.Set(new Keccak("1111111111111111111111111111111ddddddddddddddddddddddddddddddddd"), null);
            tree.UpdateRootHash();
            tree.Commit();
        }
        
        [Test]
        public void Scenario_traverse_leaf_update_with_extension()
        {
            MemDb db = new MemDb();
            StateTree tree = new StateTree(db);
            tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111111111111111111111111111111"), _account0);
            tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000000000000000000000000000000"), _account1);
            tree.UpdateRootHash();
            tree.Commit();
        }

        [Test]
        public void Scenario_traverse_leaf_delete_matching_leaf()
        {
            MemDb db = new MemDb();
            StateTree tree = new StateTree(db);
            tree.Set(new Keccak("1111111111111111111111111111111111111111111111111111111111111111"), _account0);
            Account account = tree.Get(new Keccak("1111111111111111111111111111111111111111111111111111111111111111"));
            Assert.NotNull(account);
            tree.UpdateRootHash();
            tree.Commit();
        }
        
        [Test]
        public void Scenario_traverse_leaf_read_missing()
        {
            MemDb db = new MemDb();
            StateTree tree = new StateTree(db);
            tree.Set(new Keccak("1111111111111111111111111111111111111111111111111111111111111111"), _account0);
            Account account = tree.Get(new Keccak("111111111111111111111111111111111111111111111111111111111ddddddd"));
            Assert.Null(account);
            tree.UpdateRootHash();
            tree.Commit();
        }
        
        [Test]
        public void Scenario_traverse_branch_update_missing()
        {
            MemDb db = new MemDb();
            StateTree tree = new StateTree(db);
            tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000"), _account0);
            tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111"), _account1);
            tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb22222"), _account2);
            tree.UpdateRootHash();
            tree.Commit();
        }
        
        [Test]
        public void Scenario_traverse_branch_read_missing()
        {
            MemDb db = new MemDb();
            StateTree tree = new StateTree(db);
            tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000"), _account0);
            tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111"), _account1);
            Account account = tree.Get(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb22222"));
            Assert.Null(account);
            tree.UpdateRootHash();
            tree.Commit();
        }
        
        [Test]
        public void Scenario_traverse_branch_delete_missing()
        {
            MemDb db = new MemDb();
            StateTree tree = new StateTree(db);
            tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb00000"), _account0);
            tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb11111"), _account1);
            tree.Set(new Keccak("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeb22222"), null);
            tree.UpdateRootHash();
            tree.Commit();
        }
        
        [Test]
        public void Minimal_hashes_when_setting_on_empty()
        {
            MemDb db = new MemDb();
            StateTree tree = new StateTree(db);
            tree.Set(TestObject.AddressA, _account0);
            tree.Set(TestObject.AddressB, _account0);
            tree.Set(TestObject.AddressC, _account0);
            tree.Commit();
            Assert.AreEqual(5, Metrics.TreeNodeHashCalculations, "hashes"); // branch, branch, three leaves
        }
        
        [Test]
        public void Minimal_encodings_when_setting_on_empty()
        {
            MemDb db = new MemDb();
            StateTree tree = new StateTree(db);
            tree.Set(TestObject.AddressA, _account0);
            tree.Set(TestObject.AddressB, _account0);
            tree.Set(TestObject.AddressC, _account0);
            tree.Commit();
            Assert.AreEqual(5, Metrics.TreeNodeRlpEncodings, "encodings"); // branch, branch, three leaves
        }
        
        [Test]
        public void Zero_decodings_when_setting_on_empty()
        {
            MemDb db = new MemDb();
            StateTree tree = new StateTree(db);
            tree.Set(TestObject.AddressA, _account0);
            tree.Set(TestObject.AddressB, _account0);
            tree.Set(TestObject.AddressC, _account0);
            tree.Commit();
            Assert.AreEqual(0, Metrics.TreeNodeRlpDecodings, "decodings");
        }
        
        [Test]
        public void No_writes_on_continues_update()
        {
            MemDb db = new MemDb();
            StateTree tree = new StateTree(db);
            tree.Set(TestObject.AddressA, _account0);
            tree.Set(TestObject.AddressA, _account1);
            tree.Set(TestObject.AddressA, _account2);
            tree.Set(TestObject.AddressA, _account3);
            tree.Commit();
            Assert.AreEqual(1, db.WritesCount, "writes"); // extension, branch, two leaves
        }
        
        [Ignore("This is not critical")]
        [Test]
        public void No_writes_on_reverted_update()
        {
            MemDb db = new MemDb();
            StateTree tree = new StateTree(db);
            tree.Set(TestObject.AddressA, _account0);
            tree.Commit();
            Assert.AreEqual(1, db.WritesCount, "writes before"); // extension, branch, two leaves
            tree.Set(TestObject.AddressA, _account1);
            tree.Set(TestObject.AddressA, _account0);
            tree.Commit();
            Assert.AreEqual(1, db.WritesCount, "writes after"); // extension, branch, two leaves
        }
        
        [Test]
        public void No_writes_without_commit()
        {
            MemDb db = new MemDb();
            StateTree tree = new StateTree(db);
            tree.Set(TestObject.AddressA, _account0);
            Assert.AreEqual(0, db.WritesCount, "writes");
        }
        
        [Test]
        public void Can_ask_about_root_hash_without_commiting()
        {
            MemDb db = new MemDb();
            StateTree tree = new StateTree(db);
            tree.Set(TestObject.AddressA, _account0);
            tree.UpdateRootHash();
            Assert.AreEqual("0x545a417202afcb10925b2afddb70a698710bb1cf4ab32942c42e9f019d564fdc", tree.RootHash.ToString(true));
        }
        
        [Test]
        public void Can_ask_about_root_hash_without_when_emptied()
        {
            MemDb db = new MemDb();
            StateTree tree = new StateTree(db);
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
        }
        
        [Test]
        public void hash_empty_tree_root_hash_initially()
        {
            MemDb db = new MemDb();
            StateTree tree = new StateTree(db);
            Assert.AreEqual(PatriciaTree.EmptyTreeHash, tree.RootHash);
        }
    }
}