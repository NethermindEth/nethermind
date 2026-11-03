// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Db;

public enum FlatLayout
{
    Flat,
    FlatInTrie,
    PreimageFlatV1,
    PreimageFlat,

    /// <summary>
    /// Accounts, storage and the state pointer in a memory-mapped Paprika file; trie nodes in RocksDB behind the trie node
    /// log, which this layout requires.
    /// </summary>
    PaprikaFlat,
}
