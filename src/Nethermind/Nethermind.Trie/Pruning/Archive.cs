// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

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
    }

    public class ByPathArchive : IByPathPersistenceStrategy
    {
        private ByPathArchive() { }

        public static ByPathArchive Instance { get; } = new();

        public (long blockNumber, Hash256 stateRoot)? GetBlockToPersist(long currentBlockNumber, Hash256 currentStateRoot)
        {
            return (currentBlockNumber, currentStateRoot);
        }
    }
}
