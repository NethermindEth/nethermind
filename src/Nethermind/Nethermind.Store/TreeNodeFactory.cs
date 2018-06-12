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

namespace Nethermind.Store
{
    internal static class TreeNodeFactory
    {
        public static TrieNode CreateBranch()
        {
            return new TrieNode(NodeType.Branch);
        }

        public static TrieNode CreateLeaf(HexPrefix key, byte[] value)
        {
            TrieNode node = new TrieNode(NodeType.Leaf);
            node.Key = key;
            node.Value = value;
            return node;
        }

        public static TrieNode CreateExtension(HexPrefix key)
        {
            TrieNode node = new TrieNode(NodeType.Extension);
            node.Key = key;
            return node;
        }

        public static TrieNode CreateExtension(HexPrefix key, TrieNode child)
        {
            TrieNode node = new TrieNode(NodeType.Extension);
            node.SetChild(0, child);
            node.Key = key;
            return node;
        }
    }
}