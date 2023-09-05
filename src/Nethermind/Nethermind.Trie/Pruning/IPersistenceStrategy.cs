// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Trie.Pruning
{
    public interface IPersistenceStrategy
    {
        bool ShouldPersist(long blockNumber);
        bool ShouldPersist(long currentBlockNumber, out long targetBlockNumber);
    }
}
