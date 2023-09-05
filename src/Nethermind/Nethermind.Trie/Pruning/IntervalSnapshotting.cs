// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Trie.Pruning
{
    public class ConstantInterval : IPersistenceStrategy
    {
        private readonly long _snapshotInterval;

        public ConstantInterval(long snapshotInterval)
        {
            _snapshotInterval = snapshotInterval;
        }

        public bool ShouldPersist(long blockNumber)
        {
            return blockNumber % _snapshotInterval == 0;
        }

        public bool ShouldPersist(long currentBlockNumber, out long targetBlockNumber)
        {
            targetBlockNumber = currentBlockNumber;
            return currentBlockNumber % _snapshotInterval == 0;
        }
    }
}
