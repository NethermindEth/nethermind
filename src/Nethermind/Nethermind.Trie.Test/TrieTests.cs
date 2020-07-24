using System;
using FluentAssertions;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
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
        public void When_committing_one_root_we_decrease_the_refs_and_move_nodes_somewhere()
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
        public void When_persisting_from_commit_queue_we_drop_zero_refs()
        {
            // in the front of the commit queue is the oldest block
            // and within this block we have leaves first and then everything else
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
        public void When_persisting_from_commit_queue_we_persists_non_zero_refs()
        {
            throw new NotImplementedException();
        }
        
        [Test]
        public void Deletes_just_decrease_references_as_we_do_not_persist_ref_count()
        {
            // or we do?
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
        
        private static byte[] keyA = Bytes.FromHexString("000000000000000aa");
        private static byte[] keyB = Bytes.FromHexString("000000000000000bb");

        [Test]
        public void Single_leaf()
        {
            MemDb memDb = new MemDb();
            TreeCommitter treeCommitter = new TreeCommitter(memDb, LimboLogs.Instance, 1.MB());
            PatriciaTree patriciaTree = new PatriciaTree(treeCommitter);
            patriciaTree.Set(keyA, _longLeaf1);
            patriciaTree.Commit(0);
            treeCommitter.Flush();

            // leaf (root)
            memDb.Keys.Should().HaveCount(1);
        }
        
        [Test]
        public void Single_leaf_update()
        {
            MemDb memDb = new MemDb();
            TreeCommitter treeCommitter = new TreeCommitter(memDb, LimboLogs.Instance, 1.MB());
            PatriciaTree patriciaTree = new PatriciaTree(treeCommitter);
            patriciaTree.Set(keyA, _longLeaf1);
            patriciaTree.Commit(0);
            patriciaTree.Set(keyA, _longLeaf2);
            patriciaTree.Commit(1);
            treeCommitter.Flush();

            // leaf (root)
            memDb.Keys.Should().HaveCount(1);
            
            PatriciaTree checkTree = CreateCheckTree(memDb, patriciaTree);
            checkTree.Get(keyA).Should().NotBeEquivalentTo(_longLeaf1);
            checkTree.Get(keyA).Should().BeEquivalentTo(_longLeaf2);
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
            patriciaTree.Set(keyA, _longLeaf1);
            patriciaTree.Set(keyB, _longLeaf2);
            patriciaTree.Commit(0);
            treeCommitter.Flush();
            
            memDb.Keys.Should().HaveCount(4);
        }
        
        [Test]
        public void Extension_with_branch_with_two_same_children()
        {
            MemDb memDb = new MemDb();
            TreeCommitter treeCommitter = new TreeCommitter(memDb, LimboLogs.Instance, 1.MB());
            PatriciaTree patriciaTree = new PatriciaTree(treeCommitter);
            patriciaTree.Set(keyA, _longLeaf1);
            patriciaTree.Set(keyB, _longLeaf1);
            patriciaTree.Commit(0);
            treeCommitter.Flush();
            
            memDb.Keys.Should().HaveCount(3);
        }
        
        [Test]
        public void When_branch_with_two_different_children_change_one_and_change_back_next_block()
        {
            MemDb memDb = new MemDb();
            TreeCommitter treeCommitter = new TreeCommitter(memDb, LimboLogs.Instance, 1.MB());
            PatriciaTree patriciaTree = new PatriciaTree(treeCommitter);
            patriciaTree.Set(keyA, _longLeaf1);
            patriciaTree.Set(keyB, _longLeaf2);
            patriciaTree.UpdateRootHash();
            patriciaTree.Commit(0);
            
            patriciaTree.Set(keyA, _longLeaf3);
            patriciaTree.Set(keyA, _longLeaf1);
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
            patriciaTree.Set(keyA, _longLeaf1);
            patriciaTree.Set(keyB, _longLeaf1);
            patriciaTree.UpdateRootHash();
            patriciaTree.Commit(0);
            
            patriciaTree.Set(keyA, _longLeaf3);
            patriciaTree.Set(keyA, _longLeaf1);
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