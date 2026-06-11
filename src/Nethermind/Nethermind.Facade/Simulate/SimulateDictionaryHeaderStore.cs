// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Blockchain.Headers;
using Nethermind.Core;
using Nethermind.Core.Collections;
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
    private readonly Dictionary<Hash256AsKey, BlockHeader> _headerDict = [];
    private readonly Dictionary<Hash256AsKey, ulong> _blockNumberDict = [];

    public void Insert(BlockHeader header)
    {
        Hash256 hash = header.Hash ?? throw new InvalidOperationException("Cannot cache a header without a calculated hash.");
        _headerDict[hash] = header;
        InsertBlockNumber(hash, header.Number);
    }

    public void BulkInsert(IReadOnlyList<BlockHeader> headers)
    {
        foreach (BlockHeader header in headers)
        {
            Insert(header);
        }
    }

    public BlockHeader? Get(Hash256 blockHash, bool shouldCache = false, ulong? blockNumber = null)
    {
        if (_headerDict.TryGetValue(blockHash, out BlockHeader? header))
        {
            return header;
        }

        blockNumber ??= GetBlockNumber(blockHash);

        header = readonlyBaseHeaderStore.Get(blockHash, false, blockNumber);
        if (header is not null && shouldCache)
        {
            Cache(header);
        }
        return header;
    }

    public void Cache(BlockHeader header) => Insert(header);

    public void Delete(Hash256 blockHash)
    {
        _headerDict.Remove(blockHash);
        _blockNumberDict.Remove(blockHash);
    }

    public void InsertBlockNumber(Hash256 blockHash, ulong blockNumber) => _blockNumberDict[blockHash] = blockNumber;

    public ulong? GetBlockNumber(Hash256 blockHash) =>
        _blockNumberDict.TryGetValue(blockHash, out ulong blockNumber) ? blockNumber : readonlyBaseHeaderStore.GetBlockNumber(blockHash);

    public IOwnedReadOnlyList<BlockHeader> FindReversedHeaders(ulong endBlockNumber, Hash256 endBlockHash, int count)
    {
        BlockHeader? cursor = Get(endBlockHash, shouldCache: false, blockNumber: endBlockNumber);
        if (cursor is null) return ArrayPoolList<BlockHeader>.Empty();

        ArrayPoolList<BlockHeader> result = new(count) { cursor };
        while (result.Count < count && cursor.ParentHash is not null && cursor.Number > 0)
        {
            cursor = Get(cursor.ParentHash, shouldCache: false, blockNumber: cursor.Number - 1);
            if (cursor is null) break;
            result.Add(cursor);
        }

        result.AsSpan().Reverse();
        return result;
    }

    public BlockHeader? Get(Hash256 blockHash, ulong? blockNumber = null) => Get(blockHash, true, blockNumber);
}
