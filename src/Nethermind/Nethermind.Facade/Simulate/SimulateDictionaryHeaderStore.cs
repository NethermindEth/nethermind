// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Blockchain.Headers;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Facade.Simulate;

/// <summary>
/// This type is needed for two things:
///  - Bypass issue of networking compatibility and RLPs not supporting BaseFeePerGas of 0
///  - Improve performance to get faster local caching without re-encoding of data in Simulate blocks
/// </summary>
/// <param name="readonlyBaseHeaderStore"></param>
public class SimulateDictionaryHeaderStore(IHeaderStore readonlyBaseHeaderStore) : IHeaderStore
{
    private readonly Dictionary<Hash256AsKey, BlockHeader> _headerDict = new();
    private readonly Dictionary<Hash256AsKey, long> _blockNumberDict = new();

    public void Insert(BlockHeader header)
    {
        _headerDict[header.Hash] = header;
        InsertBlockNumber(header.Hash, header.Number);
    }

    public void BulkInsert(IReadOnlyList<BlockHeader> headers)
    {
        foreach (var header in headers)
        {
            Insert(header);
        }
    }

    public BlockHeader? Get(Hash256 blockHash, out bool fromCache, bool shouldCache = false, long? blockNumber = null)
    {
        blockNumber ??= GetBlockNumber(blockHash);

        if (blockNumber.HasValue && _headerDict.TryGetValue(blockHash, out BlockHeader? header))
        {
            fromCache = true;
            if (shouldCache)
            {
                InsertBlockNumber(header.Hash, header.Number);
            }
            return header;
        }

        fromCache = false;
        header = readonlyBaseHeaderStore.Get(blockHash, false, blockNumber);
        if (header is not null && shouldCache)
        {
            Cache(header);
        }
        return header;
    }

    public void Cache(BlockHeader header, bool isCanonical = false)
    {
        Insert(header);
    }

    public void Delete(Hash256 blockHash)
    {
        _headerDict.Remove(blockHash);
        _blockNumberDict.Remove(blockHash);
    }

    public void InsertBlockNumber(Hash256 blockHash, long blockNumber)
    {
        _blockNumberDict[blockHash] = blockNumber;
    }

    public long? GetBlockNumber(Hash256 blockHash)
    {
        return _blockNumberDict.TryGetValue(blockHash, out var blockNumber) ? blockNumber : readonlyBaseHeaderStore.GetBlockNumber(blockHash);
    }

    public Hash256? GetBlockHash(long blockNumber) => null;

    public BlockHeader? GetFromCache(Hash256 blockHash) => null;

    public void CacheBlockHash(long blockNumber, Hash256 blockHash) { }
}
