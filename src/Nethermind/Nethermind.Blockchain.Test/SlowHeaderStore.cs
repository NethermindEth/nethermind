// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using Nethermind.Blockchain.Headers;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain.Test;

public class SlowHeaderStore(IHeaderStore headerStore) : IHeaderStore
{
    public long SlowBlockNumber { get; set; } = 100;

    public BlockHeader? Get(Hash256 blockHash, long? blockNumber = null)
    {
        if (blockNumber < SlowBlockNumber) Thread.Sleep(10);
        return headerStore.Get(blockHash, blockNumber);
    }

    public void Insert(BlockHeader header) => headerStore.Insert(header);
    public void BulkInsert(IReadOnlyList<BlockHeader> headers) => headerStore.BulkInsert(headers);
    public BlockHeader? Get(Hash256 blockHash, bool shouldCache, long? blockNumber = null) => headerStore.Get(blockHash, shouldCache, blockNumber);
    public void Cache(BlockHeader header) => headerStore.Cache(header);
    public void Delete(Hash256 blockHash) => headerStore.Delete(blockHash);
    public void InsertBlockNumber(Hash256 blockHash, long blockNumber) => headerStore.InsertBlockNumber(blockHash, blockNumber);
    public long? GetBlockNumber(Hash256 blockHash) => headerStore.GetBlockNumber(blockHash);
}
