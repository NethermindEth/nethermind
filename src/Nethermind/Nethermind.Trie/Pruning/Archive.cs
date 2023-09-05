// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Trie.Pruning
{
    public class Archive : IPersistenceStrategy
    {
        private Archive() { }

        public static Archive Instance { get; } = new();

        public bool ShouldPersist(long blockNumber)
        {
            return true;
        }

        public bool ShouldPersist(long currentBlockNumber, out long targetBlockNumber)
        {
            targetBlockNumber = currentBlockNumber;
            return true;
        }
    }
}
