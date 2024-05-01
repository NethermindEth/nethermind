// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using Nethermind.State;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test
{
    [TestFixture, Parallelizable(ParallelScope.All)]
    public class BlockhashProviderTests
    {
        private static IWorldState CreateWorldState()
        {
            var trieStore = new TrieStore(new MemDb(), LimboLogs.Instance);
            var worldState = new WorldState(trieStore, new MemDb(), LimboLogs.Instance);
            worldState.CreateAccount(Eip2935Constants.BlockHashHistoryAddress, 0, 1);
            worldState.Commit(Frontier.Instance);
            return worldState;
        }
        private static BlockhashProvider CreateBlockHashProvider(IBlockFinder tree, IReleaseSpec spec)
        {
            IWorldState worldState = CreateWorldState();
            BlockhashProvider provider = new(tree, new TestSpecProvider(spec), worldState, LimboLogs.Instance);
            return provider;
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Can_get_parent_only_headers()
        {
            const int chainLength = 512;

            Block genesis = Build.A.Block.Genesis.TestObject;

            BlockTree tree = Build.A.BlockTree(genesis).OfHeadersOnly.OfChainLength(chainLength).TestObject;

            BlockhashProvider provider = CreateBlockHashProvider(tree, Frontier.Instance);
            BlockHeader? head = tree.FindHeader(chainLength - 1, BlockTreeLookupOptions.None);
            Block current = Build.A.Block.WithParent(head!).TestObject;
            Hash256? result = provider.GetBlockhash(current.Header, chainLength - 1);
            Assert.That(result, Is.EqualTo(head?.Hash));
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Can_lookup_up_to_256_before_with_headers_only()
        {
            const int chainLength = 512;

            Block genesis = Build.A.Block.Genesis.TestObject;
            BlockTree tree = Build.A.BlockTree(genesis).OfHeadersOnly.OfChainLength(chainLength).TestObject;

            BlockhashProvider provider = CreateBlockHashProvider(tree, Frontier.Instance);
            BlockHeader head = tree.FindHeader(chainLength - 1, BlockTreeLookupOptions.None)!;
            Block current = Build.A.Block.WithParent(head).TestObject;
            Hash256? result = provider.GetBlockhash(current.Header, chainLength - 256);
            Assert.That(result, Is.EqualTo(tree.FindHeader(256, BlockTreeLookupOptions.None)!.Hash));
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Can_lookup_up_to_256_before_with_headers_only_and_competing_branches()
        {
            const int chainLength = 512;

            Block genesis = Build.A.Block.Genesis.TestObject;
            BlockTree tree = Build.A.BlockTree(genesis).OfHeadersOnly.OfChainLength(out Block headBlock, chainLength).OfChainLength(out Block _, chainLength, 1).TestObject;

            BlockhashProvider provider = CreateBlockHashProvider(tree, Frontier.Instance);
            Block current = Build.A.Block.WithParent(headBlock).TestObject;
            long lookupNumber = chainLength - 256;
            Hash256? result = provider.GetBlockhash(current.Header, lookupNumber);
            Assert.NotNull(result);
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Can_lookup_up_to_256_before_soon_after_fast_sync()
        {
            const int chainLength = 512;

            Block genesis = Build.A.Block.Genesis.TestObject;
            BlockTree tree = Build.A.BlockTree(genesis).OfHeadersOnly.OfChainLength(out Block headBlock, chainLength).OfChainLength(out Block _, chainLength, 1).TestObject;

            BlockhashProvider provider = CreateBlockHashProvider(tree, Frontier.Instance);
            Block current = Build.A.Block.WithParent(headBlock).TestObject;
            tree.SuggestBlock(current);
            tree.UpdateMainChain(current);
            long lookupNumber = chainLength - 256;
            Hash256? result = provider.GetBlockhash(current.Header, lookupNumber);
            Assert.NotNull(result);
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Can_lookup_up_to_256_before_some_blocks_after_fast_sync()
        {
            const int chainLength = 512;

            Block genesis = Build.A.Block.Genesis.TestObject;
            BlockTree tree = Build.A.BlockTree(genesis).OfHeadersOnly.OfChainLength(out Block headBlock, chainLength).OfChainLength(out Block _, chainLength, 1).TestObject;

            BlockhashProvider provider = CreateBlockHashProvider(tree, Frontier.Instance);

            Block current = Build.A.Block.WithParent(headBlock).TestObject;
            for (int i = 0; i < 6; i++)
            {
                tree.SuggestBlock(current);
                tree.UpdateMainChain(current);
                current = Build.A.Block.WithParent(current).TestObject;
            }

            long lookupNumber = current.Number - 256;
            Hash256? result = provider.GetBlockhash(current.Header, lookupNumber);
            Assert.NotNull(result);
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Can_handle_non_main_chain_in_fast_sync()
        {
            const int chainLength = 512;

            Block genesis = Build.A.Block.Genesis.TestObject;
            BlockTree tree = Build.A.BlockTree(genesis).OfHeadersOnly.OfChainLength(out Block headBlock, chainLength).OfChainLength(out Block _, chainLength, 1).TestObject;
            Block current = Build.A.Block.WithParent(headBlock).TestObject;
            for (int i = 0; i < 6; i++)
            {
                tree.SuggestBlock(current);
                tree.UpdateMainChain(current);
                current = Build.A.Block.WithParent(current).TestObject;
            }

            BlockhashProvider provider = CreateBlockHashProvider(tree, Frontier.Instance);

            Hash256? result = provider.GetBlockhash(current.Header, 509);
            Assert.NotNull(result);
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Can_get_parent_hash()
        {
            const int chainLength = 512;

            Block genesis = Build.A.Block.Genesis.TestObject;

            BlockTree tree = Build.A.BlockTree(genesis).OfChainLength(chainLength).TestObject;

            BlockhashProvider provider = CreateBlockHashProvider(tree, Frontier.Instance);
            BlockHeader head = tree.FindHeader(chainLength - 1, BlockTreeLookupOptions.None)!;
            Block current = Build.A.Block.WithParent(head).TestObject;
            Hash256? result = provider.GetBlockhash(current.Header, chainLength - 1);
            Assert.That(result, Is.EqualTo(head.Hash));
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Cannot_ask_for_self()
        {
            const int chainLength = 512;

            Block genesis = Build.A.Block.Genesis.TestObject;
            BlockTree tree = Build.A.BlockTree(genesis).OfChainLength(chainLength).TestObject;

            BlockhashProvider provider = CreateBlockHashProvider(tree, Frontier.Instance);
            BlockHeader head = tree.FindHeader(chainLength - 1, BlockTreeLookupOptions.None)!;
            Block current = Build.A.Block.WithParent(head).TestObject;
            Hash256? result = provider.GetBlockhash(current.Header, chainLength);
            Assert.Null(result);
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Cannot_ask_about_future()
        {
            const int chainLength = 512;

            Block genesis = Build.A.Block.Genesis.TestObject;
            BlockTree tree = Build.A.BlockTree(genesis).OfChainLength(chainLength).TestObject;

            BlockhashProvider provider = CreateBlockHashProvider(tree, Frontier.Instance);
            BlockHeader head = tree.FindHeader(chainLength - 1, BlockTreeLookupOptions.None)!;
            Block current = Build.A.Block.WithParent(head).TestObject;
            Hash256? result = provider.GetBlockhash(current.Header, chainLength + 1);
            Assert.Null(result);
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Can_lookup_up_to_256_before()
        {
            const int chainLength = 512;

            Block genesis = Build.A.Block.Genesis.TestObject;
            BlockTree tree = Build.A.BlockTree(genesis).OfChainLength(chainLength).TestObject;

            BlockhashProvider provider = CreateBlockHashProvider(tree, Frontier.Instance);
            BlockHeader head = tree.FindHeader(chainLength - 1, BlockTreeLookupOptions.None)!;
            Block current = Build.A.Block.WithParent(head).TestObject;
            Hash256? result = provider.GetBlockhash(current.Header, chainLength - 256);
            Assert.That(result, Is.EqualTo(tree.FindHeader(256, BlockTreeLookupOptions.None)!.Hash));
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void No_lookup_more_than_256_before()
        {
            const int chainLength = 512;

            Block genesis = Build.A.Block.Genesis.TestObject;
            BlockTree tree = Build.A.BlockTree(genesis).OfChainLength(chainLength).TestObject;

            BlockhashProvider provider = CreateBlockHashProvider(tree, Frontier.Instance);
            BlockHeader head = tree.FindHeader(chainLength - 1, BlockTreeLookupOptions.None)!;
            Block current = Build.A.Block.WithParent(head).TestObject;
            Hash256? result = provider.GetBlockhash(current.Header, chainLength - 257);
            Assert.Null(result);
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void UInt_256_overflow()
        {
            const int chainLength = 128;

            Block genesis = Build.A.Block.Genesis.TestObject;
            BlockTree tree = Build.A.BlockTree(genesis).OfChainLength(chainLength).TestObject;

            BlockhashProvider provider = CreateBlockHashProvider(tree, Frontier.Instance);
            BlockHeader head = tree.FindHeader(chainLength - 1, BlockTreeLookupOptions.None)!;
            Block current = Build.A.Block.WithParent(head).TestObject;
            Hash256? result = provider.GetBlockhash(current.Header, 127);
            Assert.That(result, Is.EqualTo(head.Hash));
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Eip2935_init_block_history_and_then_get_hash()
        {
            const int chainLength = 512;

            Block genesis = Build.A.Block.Genesis.TestObject;
            BlockTree tree = Build.A.BlockTree(genesis).OfHeadersOnly.OfChainLength(chainLength).TestObject;

            BlockHeader? head = tree.FindHeader(chainLength - 1, BlockTreeLookupOptions.None);
            Block current = Build.A.Block.WithParent(head!).TestObject;

            IWorldState worldState = CreateWorldState();
            var specToUse = new OverridableReleaseSpec(Prague.Instance)
            {
                Eip2935TransitionTimestamp = current.Timestamp
            };
            var specProvider = new CustomSpecProvider(
                (new ForkActivation(0, genesis.Timestamp), Frontier.Instance),
                (new ForkActivation(0, current.Timestamp), specToUse));
            BlockhashProvider provider = new(tree, specProvider, worldState, LimboLogs.Instance);
            BlockhashStore store = new (tree, specProvider, worldState);

            store.ApplyHistoryBlockHashes(current.Header);
            worldState.Commit(specToUse);

            Hash256? result = provider.GetBlockhash(current.Header, chainLength - 1);
            Assert.That(result, Is.EqualTo(head?.Hash));

            tree.SuggestHeader(current.Header);
            head = current.Header;
            current = Build.A.Block.WithParent(head!).TestObject;
            store.ApplyHistoryBlockHashes(current.Header);
            result = provider.GetBlockhash(current.Header, chainLength);
            Assert.That(result, Is.EqualTo(head?.Hash));

            result = provider.GetBlockhash(current.Header, 0);
            Assert.That(result, Is.EqualTo(genesis.Hash));
        }
    }
}
