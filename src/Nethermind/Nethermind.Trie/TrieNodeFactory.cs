// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Buffers;

namespace Nethermind.Trie
{
    internal static class TrieNodeFactory
    {
        public static TrieNode CreateBranch()
        {
            return new(new BranchData());
        }

        public static TrieNode CreateLeaf(TrieKey path, in CappedArray<byte> value)
        {
            return new(new LeafData(path, in value));
        }

        public static TrieNode CreateExtension(TrieKey path)
        {
            return new(new ExtensionData(path));
        }

        public static TrieNode CreateExtension(TrieKey path, TrieNode child)
        {
            return new(new ExtensionData(path, child));
        }
    }
}
