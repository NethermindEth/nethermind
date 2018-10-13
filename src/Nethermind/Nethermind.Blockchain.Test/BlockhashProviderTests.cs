/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test
{
    [TestFixture]
    public class BlockhashProviderTests
    {
        [Test]
        public void Can_get_parent_hash()
        {
            const int chainLength = 512;

            Block genesis = Build.A.Block.Genesis.TestObject;
            
            BlockTree tree = Build.A.BlockTree(genesis).OfChainLength(chainLength).TestObject;

            BlockhashProvider provider = new BlockhashProvider(tree);
            BlockHeader head = tree.FindHeader(chainLength - 1);
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

            BlockhashProvider provider = new BlockhashProvider(tree);
            BlockHeader head = tree.FindHeader(chainLength - 1);
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

            BlockhashProvider provider = new BlockhashProvider(tree);
            BlockHeader head = tree.FindHeader(chainLength - 1);
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

            BlockhashProvider provider = new BlockhashProvider(tree);
            BlockHeader head = tree.FindHeader(chainLength - 1);
            Block current = Build.A.Block.WithParent(head).TestObject;
            Keccak result = provider.GetBlockhash(current.Header, chainLength - 256);
            Assert.NotNull(result);
        }

        [Test]
        public void No_lookup_more_than_256_before()
        {
            const int chainLength = 512;

            Block genesis = Build.A.Block.Genesis.TestObject;
            BlockTree tree = Build.A.BlockTree(genesis).OfChainLength(chainLength).TestObject;

            BlockhashProvider provider = new BlockhashProvider(tree);
            BlockHeader head = tree.FindHeader(chainLength - 1);
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

            BlockhashProvider provider = new BlockhashProvider(tree);
            BlockHeader head = tree.FindHeader(chainLength - 1);
            Block current = Build.A.Block.WithParent(head).TestObject;
            Keccak result = provider.GetBlockhash(current.Header, 127);
            Assert.AreEqual(head.Hash, result);
        }
    }
}
