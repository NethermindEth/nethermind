// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Blockchain.Headers;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;

namespace Nethermind.Shutter.Test;

/// <summary>
/// Header store that gives every header it returns the bloom of the receipts stored for that block.
/// </summary>
/// <remarks>
/// <see cref="ShutterApiSimulator"/> stores sequencer logs as receipts of blocks built without them, so the stored
/// headers carry no bloom for those logs and the log finder would skip the blocks. Deriving the bloom on every read
/// covers each header instance the block tree hands out, whether cached or decoded again. The receipt storage is
/// resolved lazily because it depends on the block tree, which depends on this store.
/// </remarks>
internal sealed class ReceiptBloomHeaderStore(IHeaderStore headerStore, Lazy<IReceiptStorage> receiptStorage) : IHeaderStore, IClearableCache
{
    public BlockHeader? Get(Hash256 blockHash, bool shouldCache, ulong? blockNumber = null) =>
        WithReceiptBloom(headerStore.Get(blockHash, shouldCache, blockNumber));

    BlockHeader? IHeaderFinder.Get(Hash256 blockHash, ulong? blockNumber) =>
        WithReceiptBloom(((IHeaderFinder)headerStore).Get(blockHash, blockNumber));

    public void Insert(BlockHeader header) => headerStore.Insert(header);

    public void BulkInsert(IReadOnlyList<BlockHeader> headers) => headerStore.BulkInsert(headers);

    public void Cache(BlockHeader header) => headerStore.Cache(header);

    public void Delete(Hash256 blockHash) => headerStore.Delete(blockHash);

    public void InsertBlockNumber(Hash256 blockHash, ulong blockNumber) => headerStore.InsertBlockNumber(blockHash, blockNumber);

    public ulong? GetBlockNumber(Hash256 blockHash) => headerStore.GetBlockNumber(blockHash);

    public IOwnedReadOnlyList<BlockHeader> FindReversedHeaders(ulong endBlockNumber, Hash256 endBlockHash, int count) =>
        headerStore.FindReversedHeaders(endBlockNumber, endBlockHash, count);

    public Dictionary<ValueHash256, BlockHeader> PrefetchByNumberRange(ulong fromInclusive, ulong toExclusive) =>
        headerStore.PrefetchByNumberRange(fromInclusive, toExclusive);

    public void ClearCache() => (headerStore as IClearableCache)?.ClearCache();

    private BlockHeader? WithReceiptBloom(BlockHeader? header)
    {
        if (header?.Hash is null) return header;

        TxReceipt[] receipts = receiptStorage.Value.Get(header.Hash);
        if (receipts.Length == 0) return header;

        Bloom bloom = new();
        foreach (TxReceipt receipt in receipts)
        {
            if (receipt.Logs is not null) bloom.Add(receipt.Logs);
        }

        header.Bloom = bloom;
        return header;
    }
}
