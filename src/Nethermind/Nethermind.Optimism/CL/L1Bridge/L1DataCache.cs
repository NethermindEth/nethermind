// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc.Data;

namespace Nethermind.Optimism.CL.L1Bridge;

public class L1DataCache
{
    private readonly int _capacity;
    private readonly Dictionary<ulong, L1Block> _blocksByNumber = [];
    private readonly Dictionary<Hash256, L1Block> _blocksByHash = [];
    private readonly Dictionary<Hash256, ReceiptForRpc[]> _receipts = [];
    private readonly Queue<ulong> _evictionQueue = new();
    private readonly System.Threading.Lock _syncLock = new();

    public L1DataCache(int capacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), "Cache capacity must be greater than 0.");

        _capacity = capacity;
    }

    public L1Block? GetBlockByNumber(ulong number)
    {
        lock (_syncLock)
        {
            return _blocksByNumber.TryGetValue(number, out var block) ? block : null;
        }
    }

    public L1Block? GetBlockByHash(Hash256 hash)
    {
        lock (_syncLock)
        {
            return _blocksByHash.TryGetValue(hash, out var block) ? block : null;
        }
    }

    public ReceiptForRpc[]? GetReceipts(Hash256 blockHash)
    {
        lock (_syncLock)
        {
            return _receipts.TryGetValue(blockHash, out var receipts) ? receipts : null;
        }
    }

    public void CacheData(L1Block block, ReceiptForRpc[] receipts)
    {
        lock (_syncLock)
        {
            if (_blocksByNumber.ContainsKey(block.Number))
                return;

            if (_blocksByNumber.Count >= _capacity)
                EvictOldestDataInternal();

            _blocksByNumber[block.Number] = block;
            _blocksByHash[block.Hash] = block;
            _receipts[block.Hash] = receipts;
            _evictionQueue.Enqueue(block.Number);
        }
    }

    private void EvictOldestDataInternal()
    {
        ulong oldestNumber = _evictionQueue.Dequeue();
        if (_blocksByNumber.TryGetValue(oldestNumber, out var block))
        {
            _receipts.Remove(block.Hash);
            _blocksByHash.Remove(block.Hash);
            _blocksByNumber.Remove(oldestNumber);
        }
    }
}
