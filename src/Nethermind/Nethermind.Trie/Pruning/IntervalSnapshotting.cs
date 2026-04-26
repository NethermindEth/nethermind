// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Trie.Pruning
{
    public class ConstantInterval(long snapshotInterval) : IPersistenceStrategy
    {
        public bool ShouldPersist(long blockNumber) => blockNumber % snapshotInterval == 0;

        public bool IsFullPruning => false;
    }
}
