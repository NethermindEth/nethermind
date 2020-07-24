using System;
using FluentAssertions;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Trie.Test
{
    [TestFixture]
    public class TrieTests
    {
        [Test]
        public void When_dispatching_one_root_we_decrease_the_refs_and_move_nodes_somewhere()
        {
            throw new NotImplementedException();
        }
        
        [Test]
        public void When_committing_one_root_we_mark_the_block_number_on_the_nodes()
        {
            throw new NotImplementedException();
        }
        
        [Test]
        public void And_what_with_the_storage_tries_big_question()
        {
            throw new NotImplementedException();
        }
        
        [Test]
        public void In_the_commit_queue_leaves_are_before_other_nodes()
        {
            throw new NotImplementedException();
        }

        [Test]
        public void When_persisting_from_commit_queue_we_do_not_drop_zero_refs_if_this_is_memory_induced()
        {
            // in the front of the commit queue is the oldest block
            // and within this block we have leaves first and then everything else
            throw new NotImplementedException();
        }

        [Test]
        public void When_reorganizing_we_uncommit_and_commit()
        {
            // or we do?
            throw new NotImplementedException();
        }
        
        private static readonly byte[] _longLeaf1
            = Bytes.FromHexString("0000000000000000000000000000000000000000000000000000000000000000000000000000000001");
        
        private static readonly byte[] _longLeaf2
            = Bytes.FromHexString("0000000000000000000000000000000000000000000000000000000000000000000000000000000002");
        
        private static readonly byte[] _longLeaf3
            = Bytes.FromHexString("0000000000000000000000000000000000000000000000000000000000000000000000000000000003");
        
        private static byte[] _keyA = Bytes.FromHexString("000000000000000aa");
        private static byte[] _keyB = Bytes.FromHexString("000000000000000bb");
        private static byte[] _keyC = Bytes.FromHexString("000000000000001aa");
        private static byte[] _keyD = Bytes.FromHexString("000000000000001bb");

        [Test]
        public void Single_leaf()
        {
            MemDb memDb = new MemDb();
            TreeCommitter treeCommitter = new TreeCommitter(memDb, LimboLogs.Instance, 1.MB());
            PatriciaTree patriciaTree = new PatriciaTree(treeCommitter);
            patriciaTree.Set(_keyA, _longLeaf1);
            patriciaTree.Commit(0);
            treeCommitter.Flush();

            // leaf (root)
            memDb.Keys.Should().HaveCount(1);
        }
        
        [Test]
        public void Single_leaf_update_same_block()
        {
            MemDb memDb = new MemDb();
            TreeCommitter treeCommitter = new TreeCommitter(memDb, LimboLogs.Instance, 1.MB());
            PatriciaTree patriciaTree = new PatriciaTree(treeCommitter);
            patriciaTree.Set(_keyA, _longLeaf1);
            patriciaTree.Set(_keyA, _longLeaf2);
            patriciaTree.Commit(0);
            treeCommitter.Flush();

            // leaf (root)
            memDb.Keys.Should().HaveCount(1);
            
            PatriciaTree checkTree = CreateCheckTree(memDb, patriciaTree);
            checkTree.Get(_keyA).Should().NotBeEquivalentTo(_longLeaf1);
            checkTree.Get(_keyA).Should().BeEquivalentTo(_longLeaf2);
        }
        
        [Test]
        public void Single_leaf_update_next_blocks()
        {
            MemDb memDb = new MemDb();
            TreeCommitter treeCommitter = new TreeCommitter(memDb, LimboLogs.Instance, 1.MB());
            PatriciaTree patriciaTree = new PatriciaTree(treeCommitter);
            patriciaTree.Set(_keyA, _longLeaf1);
            patriciaTree.Commit(0);
            patriciaTree.Set(_keyA, _longLeaf2);
            patriciaTree.Commit(1);
            patriciaTree.UpdateRootHash();
            treeCommitter.Flush();

            // leaf (root)
            memDb.Keys.Should().HaveCount(1);
            
            PatriciaTree checkTree = CreateCheckTree(memDb, patriciaTree);
            checkTree.Get(_keyA).Should().NotBeEquivalentTo(_longLeaf1);
            checkTree.Get(_keyA).Should().BeEquivalentTo(_longLeaf2);
        }
        
        [Test]
        public void Single_leaf_delete_same_block()
        {
            MemDb memDb = new MemDb();
            TreeCommitter treeCommitter = new TreeCommitter(memDb, LimboLogs.Instance, 1.MB());
            PatriciaTree patriciaTree = new PatriciaTree(treeCommitter);
            patriciaTree.Set(_keyA, _longLeaf1);
            patriciaTree.Set(_keyA, Array.Empty<byte>());
            patriciaTree.Commit(0);
            treeCommitter.Flush();

            // leaf (root)
            memDb.Keys.Should().HaveCount(0);
            
            PatriciaTree checkTree = CreateCheckTree(memDb, patriciaTree);
            checkTree.Get(_keyA).Should().BeNull();
        }
        
        [Test]
        public void Single_leaf_delete_next_block()
        {
            MemDb memDb = new MemDb();
            TreeCommitter treeCommitter = new TreeCommitter(memDb, LimboLogs.Instance, 1.MB());
            PatriciaTree patriciaTree = new PatriciaTree(treeCommitter);
            patriciaTree.Set(_keyA, _longLeaf1);
            patriciaTree.Commit(0);
            patriciaTree.Set(_keyA, Array.Empty<byte>());
            patriciaTree.Commit(1);
            patriciaTree.UpdateRootHash();
            treeCommitter.Flush();

            // leaf (root)
            memDb.Keys.Should().HaveCount(0);
            
            PatriciaTree checkTree = CreateCheckTree(memDb, patriciaTree);
            checkTree.Get(_keyA).Should().BeNull();
        }
        
        [Test]
        public void Branch_with_branch_and_leaf()
        {
            MemDb memDb = new MemDb();
            TreeCommitter treeCommitter = new TreeCommitter(memDb, LimboLogs.Instance, 1.MB());
            PatriciaTree patriciaTree = new PatriciaTree(treeCommitter);
            patriciaTree.Set(_keyA, _longLeaf1);
            patriciaTree.Set(_keyB, _longLeaf1);
            patriciaTree.Set(_keyC, _longLeaf1);
            patriciaTree.Commit(0);
            treeCommitter.Flush();

            // leaf (root)
            memDb.Keys.Should().HaveCount(6);
            PatriciaTree checkTree = CreateCheckTree(memDb, patriciaTree);
            checkTree.Get(_keyA).Should().BeEquivalentTo(_longLeaf1);
            checkTree.Get(_keyB).Should().BeEquivalentTo(_longLeaf1);
            checkTree.Get(_keyC).Should().BeEquivalentTo(_longLeaf1);
        }
        
        [Test]
        public void Two_branches_exactly_same_leaf()
        {
            MemDb memDb = new MemDb();
            TreeCommitter treeCommitter = new TreeCommitter(memDb, LimboLogs.Instance, 1.MB());
            PatriciaTree patriciaTree = new PatriciaTree(treeCommitter);
            patriciaTree.Set(_keyA, _longLeaf1);
            patriciaTree.Set(_keyB, _longLeaf1);
            patriciaTree.Set(_keyC, _longLeaf1);
            patriciaTree.Set(_keyD, _longLeaf1);
            patriciaTree.Commit(0);
            treeCommitter.Flush();

            // leaf (root)
            memDb.Keys.Should().HaveCount(5);
            PatriciaTree checkTree = CreateCheckTree(memDb, patriciaTree);
            checkTree.Get(_keyA).Should().BeEquivalentTo(_longLeaf1);
            checkTree.Get(_keyB).Should().BeEquivalentTo(_longLeaf1);
            checkTree.Get(_keyC).Should().BeEquivalentTo(_longLeaf1);
            checkTree.Get(_keyD).Should().BeEquivalentTo(_longLeaf1);
        }
        
        [Test]
        public void Two_branches_exactly_same_leaf_then_one_removed()
        {
            MemDb memDb = new MemDb();
            TreeCommitter treeCommitter = new TreeCommitter(memDb, LimboLogs.Instance, 1.MB());
            PatriciaTree patriciaTree = new PatriciaTree(treeCommitter);
            patriciaTree.Set(_keyA, _longLeaf1);
            patriciaTree.Set(_keyB, _longLeaf1);
            patriciaTree.Set(_keyC, _longLeaf1);
            patriciaTree.Set(_keyD, _longLeaf1);
            patriciaTree.Set(_keyA, Array.Empty<byte>());
            patriciaTree.Commit(0);
            treeCommitter.Flush();

            // leaf (root)
            memDb.Keys.Should().HaveCount(6);
            PatriciaTree checkTree = CreateCheckTree(memDb, patriciaTree);
            checkTree.Get(_keyA).Should().BeNull();
            checkTree.Get(_keyB).Should().BeEquivalentTo(_longLeaf1);
            checkTree.Get(_keyC).Should().BeEquivalentTo(_longLeaf1);
            checkTree.Get(_keyD).Should().BeEquivalentTo(_longLeaf1);
        }

        private static PatriciaTree CreateCheckTree(MemDb memDb, PatriciaTree patriciaTree)
        {
            PatriciaTree.NodeCache.Clear();
            PatriciaTree checkTree = new PatriciaTree(memDb);
            checkTree.RootHash = patriciaTree.RootHash;
            return checkTree;
        }

        [Test]
        public void Extension_with_branch_with_two_different_children()
        {
            MemDb memDb = new MemDb();
            TreeCommitter treeCommitter = new TreeCommitter(memDb, LimboLogs.Instance, 1.MB());
            PatriciaTree patriciaTree = new PatriciaTree(treeCommitter);
            patriciaTree.Set(_keyA, _longLeaf1);
            patriciaTree.Set(_keyB, _longLeaf2);
            patriciaTree.Commit(0);
            treeCommitter.Flush();
            
            memDb.Keys.Should().HaveCount(4);

            PatriciaTree checkTree = CreateCheckTree(memDb, patriciaTree);
            checkTree.Get(_keyA).Should().BeEquivalentTo(_longLeaf1);
            checkTree.Get(_keyB).Should().BeEquivalentTo(_longLeaf2);
        }
        
        [Test]
        public void Extension_with_branch_with_two_same_children()
        {
            MemDb memDb = new MemDb();
            TreeCommitter treeCommitter = new TreeCommitter(memDb, LimboLogs.Instance, 1.MB());
            PatriciaTree patriciaTree = new PatriciaTree(treeCommitter);
            patriciaTree.Set(_keyA, _longLeaf1);
            patriciaTree.Set(_keyB, _longLeaf1);
            patriciaTree.Commit(0);
            treeCommitter.Flush();
            
            memDb.Keys.Should().HaveCount(4);
            PatriciaTree checkTree = CreateCheckTree(memDb, patriciaTree);
            checkTree.Get(_keyA).Should().BeEquivalentTo(_longLeaf1);
            checkTree.Get(_keyB).Should().BeEquivalentTo(_longLeaf1);
        }
        
        [Test]
        public void When_branch_with_two_different_children_change_one_and_change_back_next_block()
        {
            MemDb memDb = new MemDb();
            TreeCommitter treeCommitter = new TreeCommitter(memDb, LimboLogs.Instance, 1.MB());
            PatriciaTree patriciaTree = new PatriciaTree(treeCommitter);
            patriciaTree.Set(_keyA, _longLeaf1);
            patriciaTree.Set(_keyB, _longLeaf2);
            patriciaTree.UpdateRootHash();
            patriciaTree.Commit(0);
            
            patriciaTree.Set(_keyA, _longLeaf3);
            patriciaTree.Set(_keyA, _longLeaf1);
            patriciaTree.UpdateRootHash();
            patriciaTree.Commit(1);
            
            treeCommitter.Flush();

            // extension
            // branch
            // leaf x 2
            memDb.Keys.Should().HaveCount(4);
        }
        
        [Test]
        public void When_branch_with_two_same_children_change_one_and_change_back_next_block()
        {
            MemDb memDb = new MemDb();
            TreeCommitter treeCommitter = new TreeCommitter(memDb, LimboLogs.Instance, 1.MB());
            PatriciaTree patriciaTree = new PatriciaTree(treeCommitter);
            patriciaTree.Set(_keyA, _longLeaf1);
            patriciaTree.Set(_keyB, _longLeaf1);
            patriciaTree.UpdateRootHash();
            patriciaTree.Commit(0);
            
            patriciaTree.Set(_keyA, _longLeaf3);
            patriciaTree.Set(_keyA, _longLeaf1);
            patriciaTree.UpdateRootHash();
            patriciaTree.Commit(1);
            
            treeCommitter.Flush();

            // extension
            // branch
            // leaf same x 2
            memDb.Keys.Should().HaveCount(4);
        }
        
        [Test]
        public void When_two_branches_with_two_same_children_change_one_and_change_back_next_block()
        {
            MemDb memDb = new MemDb();
            TreeCommitter treeCommitter = new TreeCommitter(memDb, LimboLogs.Instance, 1.MB());
            PatriciaTree patriciaTree = new PatriciaTree(treeCommitter);
            patriciaTree.Set(_keyA, _longLeaf1);
            patriciaTree.Set(_keyB, _longLeaf1);
            patriciaTree.Set(_keyC, _longLeaf1);
            patriciaTree.Set(_keyD, _longLeaf1);
            patriciaTree.UpdateRootHash();
            patriciaTree.Commit(0);
            
            patriciaTree.Set(_keyA, _longLeaf3);
            patriciaTree.Set(_keyA, _longLeaf1);
            patriciaTree.UpdateRootHash();
            patriciaTree.Commit(1);
            
            treeCommitter.Flush();

            // extension
            // branch
            // leaf same x 2
            memDb.Keys.Should().HaveCount(4);
        }
    }
}