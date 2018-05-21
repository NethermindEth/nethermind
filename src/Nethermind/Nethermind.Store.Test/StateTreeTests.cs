using Nethermind.Core;
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
            Assert.AreEqual(4, db.WritesCount, "writes"); // extension, branch, two leaves
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
            Assert.AreNotEqual("0x545a417202afcb10925b2afddb70a698710bb1cf4ab32942c42e9f019d564fdc", tree.RootHash);
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