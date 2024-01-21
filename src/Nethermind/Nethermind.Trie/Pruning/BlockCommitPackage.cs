// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Trie.Pruning
{
    internal class BlockCommitSet
    {
        public long BlockNumber { get; }

        public TrieNode? Root { get; private set; }

        public bool IsSealed => Root != null;

        public BlockCommitSet(long blockNumber)
        {
            BlockNumber = blockNumber;
        }

        public void Seal(TrieNode? root)
        {
            Root = root;
        }

        public override string ToString()
        {
            return $"{BlockNumber}({Root})";
        }
    }
}
