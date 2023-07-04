// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
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
    }
}
