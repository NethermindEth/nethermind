using System;
using FluentAssertions;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using NUnit.Framework;

namespace Nethermind.Trie.Test
{
    [TestFixture]
    public class TrieTests
    {
        [Test]
        public void Set_single_leaf_and_count_memory()
        {
            MemDb memDb = new MemDb();
            PatriciaTree tree = new PatriciaTree(memDb);
            tree.Set(TestItem.KeccakA.Bytes, new byte[100]);
            tree.Root.NodeType.Should().Be(NodeType.Leaf);
            tree.Commit(1);
            tree.Root.NodeType.Should().Be(NodeType.Leaf);
            // tree.MemorySize
        }
        
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
    }
}