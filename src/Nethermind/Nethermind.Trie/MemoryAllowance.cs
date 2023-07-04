// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Extensions;

namespace Nethermind.Trie
{
    public static class MemoryAllowance
    {
        public static long TrieNodeCacheMemory { get; set; } = 128.MB();

        public static int TrieNodeCacheCount => (int)(TrieNodeCacheMemory / PatriciaTree.OneNodeAvgMemoryEstimate);
    }
}
