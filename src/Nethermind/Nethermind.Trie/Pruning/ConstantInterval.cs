// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Trie.Pruning
{
    public class ConstantInterval : IPersistenceStrategy
    {
        private readonly ulong _snapshotInterval;

        public ConstantInterval(long snapshotInterval)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(snapshotInterval, 1);
            _snapshotInterval = (ulong)snapshotInterval;
        }

        public bool ShouldPersist(ulong blockNumber) => blockNumber % _snapshotInterval == 0;

        public bool IsFullPruning => false;
    }
}
