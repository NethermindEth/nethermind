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

using System.Linq;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using NUnit.Framework;

namespace Nethermind.Store.Test
{
    [TestFixture]
    public class TrieNodeTests
    {
        private TrieNode _tiniestLeaf;
        private TrieNode _heavyLeaf;

        public TrieNodeTests()
        {
            _tiniestLeaf = new TrieNode(NodeType.Leaf);
            _tiniestLeaf.Key = new HexPrefix(true, 5);
            _tiniestLeaf.Value = new byte[] {10};
            
            _heavyLeaf = new TrieNode(NodeType.Leaf);
            _heavyLeaf.Key = new HexPrefix(true, 5);
            _heavyLeaf.Value = Keccak.EmptyTreeHash.Bytes.Concat(Keccak.EmptyTreeHash.Bytes).ToArray();
        }
        
        [Test]
        public void Can_encode_decode_tiny_branch()
        {
            TrieNode trieNode = new TrieNode(NodeType.Branch);
            trieNode.SetChild(11, _tiniestLeaf);

            Rlp rlp = trieNode.RlpEncode();

            TrieNode decoded = new TrieNode(NodeType.Unknown, rlp);
            decoded.ResolveNode(null);
            TrieNode decodedTiniest = decoded.GetChild(11);
            decodedTiniest.ResolveNode(null);

            Assert.AreEqual(_tiniestLeaf.Value, decodedTiniest.Value, "value");
            Assert.AreEqual(_tiniestLeaf.Key.ToBytes(), decodedTiniest.Key.ToBytes(), "key");
        }
        
        [Test]
        public void Can_encode_decode_heavy_branch()
        {
            TrieNode trieNode = new TrieNode(NodeType.Branch);
            trieNode.SetChild(11, _heavyLeaf);

            Rlp rlp = trieNode.RlpEncode();

            TrieNode decoded = new TrieNode(NodeType.Unknown, rlp);
            decoded.ResolveNode(null);
            TrieNode decodedTiniest = decoded.GetChild(11);

            Assert.AreEqual(decoded.GetChildHash(11), decodedTiniest.Keccak, "value");
        }
        
        [Test]
        public void Can_encode_decode_tiny_extension()
        {
            TrieNode trieNode = new TrieNode(NodeType.Extension);
            trieNode.Key = new HexPrefix(false, 5);
            trieNode.SetChild(0, _tiniestLeaf);

            Rlp rlp = trieNode.RlpEncode();

            TrieNode decoded = new TrieNode(NodeType.Unknown, rlp);
            decoded.ResolveNode(null);
            TrieNode decodedTiniest = decoded.GetChild(0);
            decodedTiniest.ResolveNode(null);

            Assert.AreEqual(_tiniestLeaf.Value, decodedTiniest.Value, "value");
            Assert.AreEqual(_tiniestLeaf.Key.ToBytes(), decodedTiniest.Key.ToBytes(), "key");
        }
        
        [Test]
        public void Can_encode_decode_heavy_extension()
        {
            TrieNode trieNode = new TrieNode(NodeType.Extension);
            trieNode.Key = new HexPrefix(false, 5);
            trieNode.SetChild(0, _heavyLeaf);

            Rlp rlp = trieNode.RlpEncode();

            TrieNode decoded = new TrieNode(NodeType.Unknown, rlp);
            decoded.ResolveNode(null);
            TrieNode decodedTiniest = decoded.GetChild(0);

            Assert.AreEqual(decoded.GetChildHash(0), decodedTiniest.Keccak, "keccak");
        }

        [Test]
        public void Can_set_and_get_children_using_indexer()
        {
            TrieNode tiniest = new TrieNode(NodeType.Leaf);
            tiniest.Key = new HexPrefix(true, 5);
            tiniest.Value = new byte[] {10};

            TrieNode trieNode = new TrieNode(NodeType.Branch);
            trieNode[11] = tiniest;
            TrieNode getResult = trieNode[11];
            Assert.AreSame(tiniest, getResult);
        }
        
        [Test]
        public void Get_child_hash_works_on_hashed_child_of_a_branch()
        {
            TrieNode trieNode = new TrieNode(NodeType.Branch);
            trieNode[11] = _heavyLeaf;
            Rlp rlp = trieNode.RlpEncode();
            TrieNode decoded = new TrieNode(NodeType.Branch, rlp);
            
            Keccak getResult = decoded.GetChildHash(11);
            Assert.NotNull(getResult);
        }
        
        [Test]
        public void Get_child_hash_works_on_inlined_child_of_a_branch()
        {
            TrieNode trieNode = new TrieNode(NodeType.Branch);
            trieNode[11] = _tiniestLeaf;
            Rlp rlp = trieNode.RlpEncode();
            TrieNode decoded = new TrieNode(NodeType.Branch, rlp);
            
            Keccak getResult = decoded.GetChildHash(11);
            Assert.Null(getResult);
        }
        
        [Test]
        public void Get_child_hash_works_on_hashed_child_of_an_extension()
        {
            TrieNode trieNode = new TrieNode(NodeType.Extension);
            trieNode[0] = _heavyLeaf;
            trieNode.Key = new HexPrefix(false, 5);
            Rlp rlp = trieNode.RlpEncode();
            TrieNode decoded = new TrieNode(NodeType.Extension, rlp);
            
            Keccak getResult = decoded.GetChildHash(0);
            Assert.NotNull(getResult);
        }
        
        [Test]
        public void Get_child_hash_works_on_inlined_child_of_an_extension()
        {
            TrieNode trieNode = new TrieNode(NodeType.Extension);
            trieNode[0] = _tiniestLeaf;
            trieNode.Key = new HexPrefix(false, 5);
            Rlp rlp = trieNode.RlpEncode();
            TrieNode decoded = new TrieNode(NodeType.Extension, rlp);
            
            Keccak getResult = decoded.GetChildHash(0);
            Assert.Null(getResult);
        }
    }
}