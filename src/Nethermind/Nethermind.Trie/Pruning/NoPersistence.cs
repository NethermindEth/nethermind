// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Trie.Pruning
{
    public class NoPersistence : IPersistenceStrategy
    {
        private NoPersistence() { }

        public static NoPersistence Instance { get; } = new();

        public bool ShouldPersist(long blockNumber)
        {
            return false;
        }
        public bool ShouldPersist(long currentBlockNumber, out long targetBlockNumber)
        {
            targetBlockNumber = -1;
            return false;
        }
    }
}
