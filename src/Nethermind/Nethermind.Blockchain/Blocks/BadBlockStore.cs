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
/// An access list larger than <paramref name="maxBlockAccessListSize"/> is not kept. When the kept access lists are larger
/// than <paramref name="blockAccessListBudget"/> in total, the access lists of the oldest entries are removed and their blocks stay.
/// </remarks>
public class BadBlockStore(
    IDb blockDb,
    long maxSize,
    IHeaderDecoder? headerDecoder = null,
    int maxBlockAccessListSize = BadBlockStore.MaxBlockAccessListSize,
    long blockAccessListBudget = BadBlockStore.BlockAccessListBudget) : IBadBlockStore
{
    // EIP-7928 allows gas limit / ItemCost (2000) items. At about 64 bytes per item (a 32-byte slot and a 32-byte value
    // with RLP overhead), 16 MiB holds about 260k items, which is the limit of a block with a gas limit of about 500M.
    public const int MaxBlockAccessListSize = 16 * 1024 * 1024;

    // Each read of the store copies every kept access list, so 100 entries at the per-entry limit would be too much.
    public const long BlockAccessListBudget = 128 * 1024 * 1024;

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

        if (bal is null || bal.Length > maxBlockAccessListSize)
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
        if (bal is not null) TrimBlockAccessLists(writeFlags);
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

    private void TrimBlockAccessLists(WriteFlags writeFlags)
    {
        long total = 0;
        using ArrayPoolList<(byte[] Key, int BlockLength, int BalLength)> entries = new((int)Math.Max(blockDb.EstimatedCount, 1));
        foreach (KeyValuePair<byte[], byte[]> entry in blockDb.GetAll(true))
        {
            int blockLength = new RlpReader(entry.Value).PeekNextRlpLength();
            int balLength = entry.Value.Length - blockLength;
            total += balLength;
            entries.Add((entry.Key, blockLength, balLength));
        }

        // Keys are ordered by block number, so the oldest blocks lose their access list first.
        for (int i = 0; i < entries.Count && total > blockAccessListBudget; i++)
        {
            (byte[] key, int blockLength, int balLength) = entries[i];
            if (balLength == 0 || blockDb.Get(key) is not { } value) continue;

            blockDb.PutSpan(key, value.AsSpan(0, blockLength), writeFlags);
            total -= balLength;
        }
    }
}
