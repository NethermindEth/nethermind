// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Blockchain.Blocks;

public class BadBlockStore(IDb blockDb, long maxSize) : IBadBlockStore
{
    private readonly BlockDecoder _blockDecoder = new();

    public void Insert(Block block, WriteFlags writeFlags = WriteFlags.None)
    {
        if (block.Hash is null)
        {
            throw new InvalidOperationException("An attempt to store a block with a null hash.");
        }

        using ArrayPoolSpan<byte> rlp = _blockDecoder.EncodeToArrayPoolSpan(block);
        blockDb.Set(block.Number, block.Hash, rlp, writeFlags);

        TruncateToMaxSize();
    }

    public IEnumerable<Block> GetAll() => blockDb.GetAllValues(true).Select(bytes =>
    {
        RlpReader ctx = new(((byte[]?)bytes ?? []));
        return _blockDecoder.Decode(ref ctx);
    });

    private void TruncateToMaxSize()
    {
        int toDelete = (int)(blockDb.GatherMetric().Size - maxSize!);
        if (toDelete > 0)
        {
            foreach (Block blockToDelete in GetAll().Take(toDelete))
            {
                Delete(blockToDelete.Number, blockToDelete.Hash);
            }
        }
    }

    private void Delete(ulong blockNumber, Hash256 blockHash) => blockDb.Delete(blockNumber, blockHash);
}
