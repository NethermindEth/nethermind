// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain.Headers;

public interface IHeaderStore
{
    void Insert(BlockHeader header);
    void BulkInsert(IReadOnlyList<BlockHeader> headers);
    BlockHeader? Get(Hash256 blockHash, bool shouldCache = true, long? blockNumber = null)
        => Get(blockHash, out _, shouldCache, blockNumber);
    BlockHeader? Get(Hash256 blockHash, out bool fromCache, bool shouldCache = true, long? blockNumber = null);
    void Cache(BlockHeader header, bool isMainChain = false);
    void Delete(Hash256 blockHash);
    void InsertBlockNumber(Hash256 blockHash, long blockNumber);
    long? GetBlockNumber(Hash256 blockHash);
    Hash256? GetBlockHash(long blockNumber);
    void CacheBlockHash(long blockNumber, Hash256 blockHash);
    BlockHeader? GetFromCache(Hash256 blockHash);
}
