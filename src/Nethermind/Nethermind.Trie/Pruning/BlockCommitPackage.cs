// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Trie.Pruning
{
    public class BlockCommitSet(long blockNumber)
    {
        public long BlockNumber { get; } = blockNumber;

        public TrieNode? Root { get; private set; }

        public bool IsSealed => Root is not null;

        public void Seal(TrieNode? root)
        {
            Root = root;
        }

        public override string ToString() => $"{BlockNumber}({Root})";
    }
}
