// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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

        public static TrieNode CreateBranch(Span<byte> pathToNode, Span<byte> storagePrefix)
        {
            TrieNode node = new(NodeType.Branch);
            node.PathToNode = pathToNode.ToArray();
            node.StoreNibblePathPrefix = storagePrefix.ToArray();
            return node;
        }

        public static TrieNode CreateLeaf(byte[] path, byte[]? value)
        {
            TrieNode node = new(NodeType.Leaf);
            node.Key = path;
            node.Value = value;
            return node;
        }

        public static TrieNode CreateLeaf(byte[] path, byte[]? value, Span<byte> pathToNode, Span<byte> storagePrefix)
        {
            Debug.Assert(path.Length + pathToNode.Length == 64);
            return new(NodeType.Leaf)
            {
                Key = path,
                Value = value,
                PathToNode = pathToNode.ToArray(),
                StoreNibblePathPrefix = storagePrefix.ToArray()
            };
        }

        public static TrieNode CreateExtension(byte[] path)
        {
            TrieNode node = new(NodeType.Extension);
            node.Key = path;
            return node;
        }

        public static TrieNode CreateExtension(byte[] path, Span<byte> pathToNode, Span<byte> storagePrefix)
        {
            TrieNode node = new(NodeType.Extension);
            node.Key = path;
            node.PathToNode = pathToNode.ToArray();
            node.StoreNibblePathPrefix = storagePrefix.ToArray();
            return node;
        }

        public static TrieNode CreateExtension(byte[] path, TrieNode child)
        {
            TrieNode node = new(NodeType.Extension);
            node.SetChild(0, child);
            node.Key = path;
            return node;
        }

        public static TrieNode CreateExtension(byte[] path, TrieNode child, Span<byte> pathToNode, Span<byte> storagePrefix)
        {
            TrieNode node = new(NodeType.Extension);
            node.SetChild(0, child);
            node.Key = path;
            node.PathToNode = pathToNode.ToArray();
            node.StoreNibblePathPrefix = storagePrefix.ToArray();
            return node;
        }
    }
}
