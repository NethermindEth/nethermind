// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Trie.Pruning
{
    internal class BlockCommitSet
    {
        public long BlockNumber { get; }

        public TrieNode? Root { get; set; }

        public bool IsSealed { get; private set; }

        public long MemorySizeOfCommittedNodes { get; set; }

        public BlockCommitSet(long blockNumber)
        {
            BlockNumber = blockNumber;
        }

        public void Seal()
        {
            IsSealed = true;
        }

        public override string ToString()
        {
            return $"{BlockNumber}({Root})";
        }
    }
}
