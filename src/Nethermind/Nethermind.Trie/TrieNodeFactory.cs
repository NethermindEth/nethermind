// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Buffers;

namespace Nethermind.Trie
{
    internal static class TrieNodeFactory
    {
        public static TrieNode CreateBranch()
        {
            TrieNode node = new(NodeType.Branch);
            return node;
        }

        // Used in testing code
        public static TrieNode CreateLeaf(byte[] nibbles, in CappedArray<byte> value)
        {
            return CreateLeaf(BoxedTreePath.FromNibble(nibbles), value);
        }

        // TODO: Is it faster to have a BoxedTreePath here or use a in TreePath?
        public static TrieNode CreateLeaf(BoxedTreePath path, in CappedArray<byte> value)
        {
            TrieNode node = new(NodeType.Leaf);
            node.Key = path;
            node.Value = value;
            return node;
        }

        public static TrieNode CreateExtension(BoxedTreePath path)
        {
            TrieNode node = new(NodeType.Extension);
            node.Key = path;
            return node;
        }

        // Used in testing code
        public static TrieNode CreateExtension(byte[] nibbles, TrieNode child)
        {
            return CreateExtension(BoxedTreePath.FromNibble(nibbles), child);
        }

        public static TrieNode CreateExtension(BoxedTreePath path, TrieNode child)
        {
            TrieNode node = new(NodeType.Extension);
            node.SetChild(0, child);
            node.Key = path;
            return node;
        }
    }
}
