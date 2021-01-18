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

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test
{
    [TestFixture]
    public class BlockhashProviderTests
    {
        [Test]
        public void Can_get_parent_only_headers()
        {
            const int chainLength = 512;

            Block genesis = Build.A.Block.Genesis.TestObject;

            BlockTree tree = Build.A.BlockTree(genesis).OfHeadersOnly.OfChainLength(chainLength).TestObject;

            BlockhashProvider provider = new BlockhashProvider(tree, LimboLogs.Instance);
            BlockHeader head = tree.FindHeader(chainLength - 1, BlockTreeLookupOptions.None);
            Block current = Build.A.Block.WithParent(head).TestObject;
            Keccak result = provider.GetBlockhash(current.Header, chainLength - 1);
            Assert.AreEqual(head.Hash, result);
        }

        [Test]
        public void Can_lookup_up_to_256_before_with_headers_only()
        {
            const int chainLength = 512;

            Block genesis = Build.A.Block.Genesis.TestObject;
            BlockTree tree = Build.A.BlockTree(genesis).OfHeadersOnly.OfChainLength(chainLength).TestObject;

            BlockhashProvider provider = new BlockhashProvider(tree, LimboLogs.Instance);
            BlockHeader head = tree.FindHeader(chainLength - 1, BlockTreeLookupOptions.None);
            Block current = Build.A.Block.WithParent(head).TestObject;
            Keccak result = provider.GetBlockhash(current.Header, chainLength - 256);
            Assert.AreEqual(tree.FindHeader(256, BlockTreeLookupOptions.None).Hash, result);
        }

        [Test]
        public void Can_lookup_up_to_256_before_with_headers_only_and_competing_branches()
        {
            const int chainLength = 512;

            Block genesis = Build.A.Block.Genesis.TestObject;
            BlockTree tree = Build.A.BlockTree(genesis).OfHeadersOnly.OfChainLength(out Block headBlock, chainLength).OfChainLength(out Block alternativeHeadBlock, chainLength, 1).TestObject;

            BlockhashProvider provider = new BlockhashProvider(tree, LimboLogs.Instance);
            Block current = Build.A.Block.WithParent(headBlock).TestObject;
            long lookupNumber = chainLength - 256;
            Keccak result = provider.GetBlockhash(current.Header, lookupNumber);
            Assert.NotNull(result);
        }
        
        [Test]
        public void Can_lookup_up_to_256_before_soon_after_fast_sync()
        {
            const int chainLength = 512;

            Block genesis = Build.A.Block.Genesis.TestObject;
            BlockTree tree = Build.A.BlockTree(genesis).OfHeadersOnly.OfChainLength(out Block headBlock, chainLength).OfChainLength(out Block alternativeHeadBlock, chainLength, 1).TestObject;

            BlockhashProvider provider = new BlockhashProvider(tree, LimboLogs.Instance);
            Block current = Build.A.Block.WithParent(headBlock).TestObject;
            tree.SuggestBlock(current);
            tree.UpdateMainChain(current);
            long lookupNumber = chainLength - 256;
            Keccak result = provider.GetBlockhash(current.Header, lookupNumber);
            Assert.NotNull(result);
        }
        
        [Test]
        public void Can_lookup_up_to_256_before_some_blocks_after_fast_sync()
        {
            const int chainLength = 512;

            Block genesis = Build.A.Block.Genesis.TestObject;
            BlockTree tree = Build.A.BlockTree(genesis).OfHeadersOnly.OfChainLength(out Block headBlock, chainLength).OfChainLength(out Block alternativeHeadBlock, chainLength, 1).TestObject;

            BlockhashProvider provider = new BlockhashProvider(tree, LimboLogs.Instance);

            Block current = Build.A.Block.WithParent(headBlock).TestObject;
            for (int i = 0; i < 6; i++)
            {
                tree.SuggestBlock(current);
                tree.UpdateMainChain(current);
                current = Build.A.Block.WithParent(current).TestObject;
            }
            
            long lookupNumber = current.Number - 256;
            Keccak result = provider.GetBlockhash(current.Header, lookupNumber);
            Assert.NotNull(result);
        }
        
        [Test]
        public void Can_handle_non_main_chain_in_fast_sync()
        {
            const int chainLength = 512;

            Block genesis = Build.A.Block.Genesis.TestObject;
            BlockTree tree = Build.A.BlockTree(genesis).OfHeadersOnly.OfChainLength(out Block headBlock, chainLength).OfChainLength(out Block alternativeHeadBlock, chainLength, 1).TestObject;
            Block current = Build.A.Block.WithParent(headBlock).TestObject;
            for (int i = 0; i < 6; i++)
            {
                tree.SuggestBlock(current);
                tree.UpdateMainChain(current);
                current = Build.A.Block.WithParent(current).TestObject;
            }
            
            BlockhashProvider provider = new BlockhashProvider(tree, LimboLogs.Instance);
            
            Keccak result = provider.GetBlockhash(current.Header, 509);
            Assert.NotNull(result);
        }

        [Test]
        public void Can_get_parent_hash()
        {
            const int chainLength = 512;

            Block genesis = Build.A.Block.Genesis.TestObject;

            BlockTree tree = Build.A.BlockTree(genesis).OfChainLength(chainLength).TestObject;

            BlockhashProvider provider = new BlockhashProvider(tree, LimboLogs.Instance);
            BlockHeader head = tree.FindHeader(chainLength - 1, BlockTreeLookupOptions.None);
            Block current = Build.A.Block.WithParent(head).TestObject;
            Keccak result = provider.GetBlockhash(current.Header, chainLength - 1);
            Assert.AreEqual(head.Hash, result);
        }

        [Test]
        public void Cannot_ask_for_self()
        {
            const int chainLength = 512;

            Block genesis = Build.A.Block.Genesis.TestObject;
            BlockTree tree = Build.A.BlockTree(genesis).OfChainLength(chainLength).TestObject;

            BlockhashProvider provider = new BlockhashProvider(tree, LimboLogs.Instance);
            BlockHeader head = tree.FindHeader(chainLength - 1, BlockTreeLookupOptions.None);
            Block current = Build.A.Block.WithParent(head).TestObject;
            Keccak result = provider.GetBlockhash(current.Header, chainLength);
            Assert.Null(result);
        }

        [Test]
        public void Cannot_ask_about_future()
        {
            const int chainLength = 512;

            Block genesis = Build.A.Block.Genesis.TestObject;
            BlockTree tree = Build.A.BlockTree(genesis).OfChainLength(chainLength).TestObject;

            BlockhashProvider provider = new BlockhashProvider(tree, LimboLogs.Instance);
            BlockHeader head = tree.FindHeader(chainLength - 1, BlockTreeLookupOptions.None);
            Block current = Build.A.Block.WithParent(head).TestObject;
            Keccak result = provider.GetBlockhash(current.Header, chainLength + 1);
            Assert.Null(result);
        }

        [Test]
        public void Can_lookup_up_to_256_before()
        {
            const int chainLength = 512;

            Block genesis = Build.A.Block.Genesis.TestObject;
            BlockTree tree = Build.A.BlockTree(genesis).OfChainLength(chainLength).TestObject;

            BlockhashProvider provider = new BlockhashProvider(tree, LimboLogs.Instance);
            BlockHeader head = tree.FindHeader(chainLength - 1, BlockTreeLookupOptions.None);
            Block current = Build.A.Block.WithParent(head).TestObject;
            Keccak result = provider.GetBlockhash(current.Header, chainLength - 256);
            Assert.AreEqual(tree.FindHeader(256, BlockTreeLookupOptions.None).Hash, result);
        }

        [Test]
        public void No_lookup_more_than_256_before()
        {
            const int chainLength = 512;

            Block genesis = Build.A.Block.Genesis.TestObject;
            BlockTree tree = Build.A.BlockTree(genesis).OfChainLength(chainLength).TestObject;

            BlockhashProvider provider = new BlockhashProvider(tree, LimboLogs.Instance);
            BlockHeader head = tree.FindHeader(chainLength - 1, BlockTreeLookupOptions.None);
            Block current = Build.A.Block.WithParent(head).TestObject;
            Keccak result = provider.GetBlockhash(current.Header, chainLength - 257);
            Assert.Null(result);
        }

        [Test]
        public void UInt_256_overflow()
        {
            const int chainLength = 128;

            Block genesis = Build.A.Block.Genesis.TestObject;
            BlockTree tree = Build.A.BlockTree(genesis).OfChainLength(chainLength).TestObject;

            BlockhashProvider provider = new BlockhashProvider(tree, LimboLogs.Instance);
            BlockHeader head = tree.FindHeader(chainLength - 1, BlockTreeLookupOptions.None);
            Block current = Build.A.Block.WithParent(head).TestObject;
            Keccak result = provider.GetBlockhash(current.Header, 127);
            Assert.AreEqual(head.Hash, result);
        }
    }
}
