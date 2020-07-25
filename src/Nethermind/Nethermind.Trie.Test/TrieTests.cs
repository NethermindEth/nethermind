using System;
using FluentAssertions;
using Nethermind.Core.Crypto;
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
        // [Test]
        // public void When_dispatching_one_root_we_decrease_the_refs_and_move_nodes_somewhere()
        // {
        //     throw new NotImplementedException();
        // }
        //
        // [Test]
        // public void When_committing_one_root_we_mark_the_block_number_on_the_nodes()
        // {
        //     throw new NotImplementedException();
        // }
        //
        // [Test]
        // public void And_what_with_the_storage_tries_big_question()
        // {
        //     throw new NotImplementedException();
        // }
        //
        // [Test]
        // public void In_the_commit_queue_leaves_are_before_other_nodes()
        // {
        //     throw new NotImplementedException();
        // }
        //
        // [Test]
        // public void When_persisting_from_commit_queue_we_do_not_drop_zero_refs_if_this_is_memory_induced()
        // {
        //     // in the front of the commit queue is the oldest block
        //     // and within this block we have leaves first and then everything else
        //     throw new NotImplementedException();
        // }
        //
        // [Test]
        // public void When_reorganizing_we_uncommit_and_commit()
        // {
        //     // or we do?
        //     throw new NotImplementedException();
        // }

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
            memDb.Keys.Should().HaveCount(2);

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
            memDb.Keys.Should().HaveCount(1);

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
        public void Branch_with_branch_and_leaf_then_deleted()
        {
            MemDb memDb = new MemDb();
            TreeCommitter treeCommitter = new TreeCommitter(memDb, LimboLogs.Instance, 1.MB());
            PatriciaTree patriciaTree = new PatriciaTree(treeCommitter);
            patriciaTree.Set(_keyA, _longLeaf1);
            patriciaTree.Set(_keyB, _longLeaf1);
            patriciaTree.Set(_keyC, _longLeaf1);
            patriciaTree.Commit(0);
            patriciaTree.Set(_keyA, Array.Empty<byte>());
            patriciaTree.Set(_keyB, Array.Empty<byte>());
            patriciaTree.Set(_keyC, Array.Empty<byte>());
            patriciaTree.Commit(1);
            patriciaTree.UpdateRootHash();
            treeCommitter.Flush();

            // leaf (root)
            memDb.Keys.Should().HaveCount(6);
            PatriciaTree checkTree = CreateCheckTree(memDb, patriciaTree);
            checkTree.Get(_keyA).Should().BeNull();
            checkTree.Get(_keyB).Should().BeNull();
            checkTree.Get(_keyC).Should().BeNull();
        }

        public void Test_add_many(int i)
        {
            MemDb memDb = new MemDb();
            TreeCommitter treeCommitter = new TreeCommitter(memDb, LimboLogs.Instance, 128.MB());
            PatriciaTree patriciaTree = new PatriciaTree(treeCommitter, Keccak.EmptyTreeHash, true, true);
            
            for (int j = 0; j < i; j++)
            {
                Keccak key = TestItem.Keccaks[j];
                byte[] value = new byte[128];
                value[^1] = (byte) j;
                patriciaTree.Set(key.Bytes, value);
            }

            patriciaTree.Commit(0);
            patriciaTree.UpdateRootHash();
            treeCommitter.Flush();

            PatriciaTree checkTree = CreateCheckTree(memDb, patriciaTree);
            for (int j = 0; j < i; j++)
            {
                Keccak key = TestItem.Keccaks[j];
                byte[] value = new byte[128];
                value[^1] = (byte) j;
                checkTree.Get(key.Bytes).Should().BeEquivalentTo(value, $@"{i} {j}");
            }
        }
        
        public void Test_try_delete_and_read_missing_nodes(int i)
        {
            MemDb memDb = new MemDb();
            TreeCommitter treeCommitter = new TreeCommitter(memDb, LimboLogs.Instance, 128.MB());
            PatriciaTree patriciaTree = new PatriciaTree(treeCommitter, Keccak.EmptyTreeHash, true, true);
            
            for (int j = 0; j < i; j++)
            {
                Keccak key = TestItem.Keccaks[j];
                byte[] value = new byte[128];
                value[^1] = (byte) j;
                patriciaTree.Set(key.Bytes, value);
            }
            
            // delete missing
            for (int j = 0; j < i; j++)
            {
                Keccak key = TestItem.Keccaks[j + 100];
                patriciaTree.Set(key.Bytes, Array.Empty<byte>());
            }

            patriciaTree.Commit(0);
            patriciaTree.UpdateRootHash();
            treeCommitter.Flush();

            PatriciaTree checkTree = CreateCheckTree(memDb, patriciaTree);
            
            // confirm nothing deleted
            for (int j = 0; j < i; j++)
            {
                Keccak key = TestItem.Keccaks[j];
                byte[] value = new byte[128];
                value[^1] = (byte) j;
                checkTree.Get(key.Bytes).Should().BeEquivalentTo(value, $@"{i} {j}");
            }
            
            // read missing
            for (int j = 0; j < i; j++)
            {
                Keccak key = TestItem.Keccaks[j + 100];
                checkTree.Get(key.Bytes).Should().BeNull();
            }
        }
        
        public void Test_update_many(int i)
        {
            MemDb memDb = new MemDb();
            TreeCommitter treeCommitter = new TreeCommitter(memDb, LimboLogs.Instance, 128.MB());
            PatriciaTree patriciaTree = new PatriciaTree(treeCommitter);
            
            for (int j = 0; j < i; j++)
            {
                Keccak key = TestItem.Keccaks[j];
                byte[] value = new byte[128];
                value[^1] = (byte) j;
                patriciaTree.Set(key.Bytes, value);
            }
            
            for (int j = 0; j < i; j++)
            {
                Keccak key = TestItem.Keccaks[j];
                byte[] value = new byte[128];
                value[^1] = (byte) (j + 1);
                patriciaTree.Set(key.Bytes, value);
            }

            patriciaTree.Commit(0);
            patriciaTree.UpdateRootHash();
            treeCommitter.Flush();

            PatriciaTree checkTree = CreateCheckTree(memDb, patriciaTree);
            for (int j = 0; j < i; j++)
            {
                Keccak key = TestItem.Keccaks[j];
                byte[] value = new byte[128];
                value[^1] = (byte) (j + 1);
                checkTree.Get(key.Bytes).Should().BeEquivalentTo(value, $@"{i} {j}");
            }
        }
        
        public void Test_update_many_next_block(int i)
        {
            MemDb memDb = new MemDb();
            TreeCommitter treeCommitter = new TreeCommitter(memDb, LimboLogs.Instance, 128.MB());
            PatriciaTree patriciaTree = new PatriciaTree(treeCommitter);
            
            for (int j = 0; j < i; j++)
            {
                Keccak key = TestItem.Keccaks[j];
                byte[] value = new byte[128];
                value[^1] = (byte) j;
                patriciaTree.Set(key.Bytes, value);
            }
            
            patriciaTree.Commit(0);
            
            for (int j = 0; j < i; j++)
            {
                Keccak key = TestItem.Keccaks[j];
                byte[] value = new byte[128];
                value[^1] = (byte) (j + 1);
                patriciaTree.Set(key.Bytes, value);
            }

            patriciaTree.Commit(1);
            patriciaTree.UpdateRootHash();
            treeCommitter.Flush();

            PatriciaTree checkTree = CreateCheckTree(memDb, patriciaTree);
            for (int j = 0; j < i; j++)
            {
                Keccak key = TestItem.Keccaks[j];
                byte[] value = new byte[128];
                value[^1] = (byte) (j + 1);
                checkTree.Get(key.Bytes).Should().BeEquivalentTo(value, $@"{i} {j}");
            }
        }
        
        public void Test_add_and_delete_many_same_block(int i)
        {
            MemDb memDb = new MemDb();
            TreeCommitter treeCommitter = new TreeCommitter(memDb, LimboLogs.Instance, 128.MB());
            PatriciaTree patriciaTree = new PatriciaTree(treeCommitter);
            
            for (int j = 0; j < i; j++)
            {
                TestContext.WriteLine($"  set {j}");
                Keccak key = TestItem.Keccaks[j];
                byte[] value = new byte[128];
                value[^1] = (byte) j;
                patriciaTree.Set(key.Bytes, value);
            }
            
            for (int j = 0; j < i; j++)
            {
                TestContext.WriteLine($"  delete {j}");
                Keccak key = TestItem.Keccaks[j];
                patriciaTree.Set(key.Bytes, Array.Empty<byte>());
            }

            patriciaTree.Commit(0);
            patriciaTree.UpdateRootHash();
            treeCommitter.Flush();

            PatriciaTree checkTree = CreateCheckTree(memDb, patriciaTree);
            for (int j = 0; j < i; j++)
            {
                Keccak key = TestItem.Keccaks[j];
                byte[] value = new byte[128];
                value[^1] = (byte) j;
                checkTree.Get(key.Bytes).Should().BeNull($@"{i} {j}");
            }
        }
        
        public void Test_add_and_delete_many_next_block(int i)
        {
            MemDb memDb = new MemDb();
            TreeCommitter treeCommitter = new TreeCommitter(memDb, LimboLogs.Instance, 128.MB());
            PatriciaTree patriciaTree = new PatriciaTree(treeCommitter);
            
            for (int j = 0; j < i; j++)
            {
                Keccak key = TestItem.Keccaks[j];
                byte[] value = new byte[128];
                value[^1] = (byte) j;
                patriciaTree.Set(key.Bytes, value);
            }
            
            patriciaTree.Commit(0);

            for (int j = 0; j < i; j++)
            {
                Keccak key = TestItem.Keccaks[j];
                patriciaTree.Set(key.Bytes, Array.Empty<byte>());
            }

            patriciaTree.Commit(1);
            patriciaTree.UpdateRootHash();
            treeCommitter.Flush();

            PatriciaTree checkTree = CreateCheckTree(memDb, patriciaTree);
            for (int j = 0; j < i; j++)
            {
                Keccak key = TestItem.Keccaks[j];
                byte[] value = new byte[128];
                value[^1] = (byte) j;
                checkTree.Get(key.Bytes).Should().BeNull($@"{i} {j}");
            }
        }

        [Test]
        public void Big_test()
        {
            // there was a case that was failing only at iteration 85 (before you change it to a smaller number)
            
            for (int i = 1; i < 100; i++)
            {
                TestContext.WriteLine(i);
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

            memDb.Keys.Should().HaveCount(4);
            PatriciaTree checkTree = CreateCheckTree(memDb, patriciaTree);
            checkTree.Get(_keyA).Should().BeEquivalentTo(_longLeaf1);
            checkTree.Get(_keyB).Should().BeEquivalentTo(_longLeaf1);
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
            
            MemDb memDb = new MemDb();
            TreeCommitter treeCommitter = new TreeCommitter(memDb, LimboLogs.Instance, 1.MB());
            PatriciaTree patriciaTree = new PatriciaTree(treeCommitter);
            patriciaTree.Set(key1, _longLeaf1);
            patriciaTree.Set(key2, _longLeaf1);
            patriciaTree.Set(key3, _longLeaf1);
            patriciaTree.UpdateRootHash();
            patriciaTree.Commit(0);
            treeCommitter.Flush();

            memDb.Keys.Should().HaveCount(7);
            PatriciaTree checkTree = CreateCheckTree(memDb, patriciaTree);
            checkTree.Get(key1).Should().BeEquivalentTo(_longLeaf1);
            checkTree.Get(key2).Should().BeEquivalentTo(_longLeaf1);
            checkTree.Get(key3).Should().BeEquivalentTo(_longLeaf1);
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
            
            MemDb memDb = new MemDb();
            TreeCommitter treeCommitter = new TreeCommitter(memDb, LimboLogs.Instance, 1.MB());
            PatriciaTree patriciaTree = new PatriciaTree(treeCommitter);
            patriciaTree.Set(key1, _longLeaf1);
            patriciaTree.Set(key2, _longLeaf1);
            patriciaTree.Set(key3, _longLeaf1);
            patriciaTree.UpdateRootHash();
            patriciaTree.Commit(0);
            patriciaTree.Set(key3, Array.Empty<byte>());
            patriciaTree.UpdateRootHash();
            patriciaTree.Commit(1);
            treeCommitter.Flush();

            memDb.Keys.Should().HaveCount(8);
            PatriciaTree checkTree = CreateCheckTree(memDb, patriciaTree);
            checkTree.Get(key1).Should().BeEquivalentTo(_longLeaf1);
            checkTree.Get(key2).Should().BeEquivalentTo(_longLeaf1);
            checkTree.Get(key3).Should().BeNull();
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

            memDb.Keys.Should().HaveCount(5);
            PatriciaTree checkTree = CreateCheckTree(memDb, patriciaTree);
            checkTree.Get(_keyA).Should().BeEquivalentTo(_longLeaf1);
            checkTree.Get(_keyB).Should().BeEquivalentTo(_longLeaf1);
            checkTree.Get(_keyC).Should().BeEquivalentTo(_longLeaf1);
            checkTree.Get(_keyD).Should().BeEquivalentTo(_longLeaf1);
        }
    }
}