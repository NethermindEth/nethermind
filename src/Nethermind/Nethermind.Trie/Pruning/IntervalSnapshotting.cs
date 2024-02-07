// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

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

    public class ByPathConstantInterval : IByPathPersistenceStrategy
    {
        private readonly long _snapshotInterval;

        public ByPathConstantInterval(long snapshotInterval)
        {
            _snapshotInterval = snapshotInterval;
        }

        public (long blockNumber, Hash256 stateRoot)? GetBlockToPersist(long currentBlockNumber, Hash256 currentStateRoot)
        {
            return currentBlockNumber % _snapshotInterval == 0 ? (currentBlockNumber, currentStateRoot) : null;
        }
    }
}
