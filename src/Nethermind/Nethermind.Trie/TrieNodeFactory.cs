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

using System.Diagnostics;
using System.Xml.XPath;

namespace Nethermind.Trie
{
    internal static class TrieNodeFactory
    {
        public static TrieNode CreateBranch()
        {
            TrieNode node = new(NodeType.Branch);
            return node;
        }

        public static TrieNode CreateLeaf(HexPrefix key, byte[]? value)
        {
            Debug.Assert(
                key.IsLeaf,
                $"{nameof(NodeType.Leaf)} should always be created with a leaf {nameof(HexPrefix)}");

            TrieNode node = new(NodeType.Leaf);
            node.Key = key;
            node.Value = value;
            return node;
        }

        public static TrieNode CreateExtension(HexPrefix key)
        {
            TrieNode node = new(NodeType.Extension);
            node.Key = key;
            return node;
        }

        public static TrieNode CreateExtension(HexPrefix key, TrieNode child)
        {
            Debug.Assert(
                key.IsExtension,
                $"{nameof(NodeType.Extension)} should always be created with an extension {nameof(HexPrefix)}");

            TrieNode node = new(NodeType.Extension);
            node.SetChild(0, child);
            node.Key = key;
            return node;
        }
    }
}
