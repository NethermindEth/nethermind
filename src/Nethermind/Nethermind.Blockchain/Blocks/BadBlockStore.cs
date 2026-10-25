// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Db;
using Nethermind.Serialization.Rlp;
using Nethermind.Serialization.Rlp.Eip7928;

namespace Nethermind.Blockchain.Blocks;

/// <remarks>
/// Each value is the block RLP, followed by the RLP of the suggested block access list when the block has one.
/// The access list is returned as <see cref="Block.EncodedBlockAccessList"/> and is not decoded.
/// An access list larger than <see cref="MaxBlockAccessListSize"/> is not kept.
/// </remarks>
public class BadBlockStore(IDb blockDb, long maxSize, IHeaderDecoder? headerDecoder = null) : IBadBlockStore
{
    // Every read of the store (hash lookups and debug_getBadBlocks) copies each kept access list, so the size is bounded.
    public const int MaxBlockAccessListSize = 1024 * 1024;

    private readonly BlockDecoder _blockDecoder = new(headerDecoder ?? new HeaderDecoder());

    public void Insert(Block block, WriteFlags writeFlags = WriteFlags.None)
    {
        if (block.Hash is null)
        {
            throw new InvalidOperationException("An attempt to store a block with a null hash.");
        }

        using ArrayPoolSpan<byte> rlp = _blockDecoder.EncodeToArrayPoolSpan(block);
        byte[]? bal = block.EncodedBlockAccessList
            ?? (block.BlockAccessList is { } blockAccessList ? BlockAccessListDecoder.EncodeToBytes(blockAccessList) : null);

        if (bal is null || bal.Length > MaxBlockAccessListSize)
        {
            blockDb.Set(block.Number, block.Hash, rlp, writeFlags);
        }
        else
        {
            using ArrayPoolSpan<byte> value = new(rlp.Length + bal.Length);
            ((ReadOnlySpan<byte>)rlp).CopyTo(value);
            bal.CopyTo(value.Slice(rlp.Length, bal.Length));
            blockDb.Set(block.Number, block.Hash, value, writeFlags);
        }

        TruncateToMaxSize();
    }

    public IEnumerable<Block> GetAll() => blockDb.GetAllValues(true).Select(bytes =>
    {
        RlpReader ctx = new(((byte[]?)bytes ?? []));
        Block block = _blockDecoder.Decode(ref ctx);
        if (ctx.Position < ctx.Length)
        {
            block.EncodedBlockAccessList = ctx.Data[ctx.Position..].ToArray();
        }

        return block;
    });

    private void TruncateToMaxSize()
    {
        int toDelete = (int)(blockDb.EstimatedCount - maxSize);
        if (toDelete > 0)
        {
            // The key is the block number followed by the hash, so the values do not need to be decoded.
            foreach (byte[] key in blockDb.GetAllKeys(true).Take(toDelete))
            {
                blockDb.Remove(key);
            }
        }
    }
}
