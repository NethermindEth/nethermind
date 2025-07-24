// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Buffers;

namespace Nethermind.Trie
{
    public static class TrieNodeFactory
    {
        public static TrieNode CreateBranch()
        {
            return new(new BranchData());
        }

        public static TrieNode CreateLeaf(byte[] path, SpanSource value)
        {
            return new(new LeafData(path, value));
        }

        public static TrieNode CreateExtension(byte[] path)
        {
            return new(new ExtensionData(path));
        }

        public static TrieNode CreateExtension(byte[] path, TrieNode child)
        {
            return new(new ExtensionData(path, child));
        }
    }
}
