// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain.Headers;

public interface IHeaderStore : IHeaderFinder
{
    void Insert(BlockHeader header);
    void BulkInsert(IReadOnlyList<BlockHeader> headers);
    BlockHeader? Get(Hash256 blockHash, bool shouldCache, long? blockNumber = null);
    void Cache(BlockHeader header);
    void Delete(Hash256 blockHash);
    void InsertBlockNumber(Hash256 blockHash, long blockNumber);
    long? GetBlockNumber(Hash256 blockHash);

    /// <summary>
    /// Returns up to <paramref name="count"/> consecutive headers ending at <paramref name="endBlockHash"/>,
    /// walking backward through parent hashes. Uses a DB iterator for bulk read when the underlying
    /// store supports <see cref="ISortedKeyValueStore"/>; otherwise falls back to per-hash lookups.
    /// The returned list is ordered oldest-first. If the chain breaks, the returned list is shorter.
    /// Returns an empty list when <paramref name="endBlockHash"/> is not found.
    /// </summary>
    IOwnedReadOnlyList<BlockHeader> FindReversedHeaders(long endBlockNumber, Hash256 endBlockHash, int count);
}
