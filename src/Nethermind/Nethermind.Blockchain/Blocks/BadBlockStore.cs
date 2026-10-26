// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Serialization.Rlp.Eip7928;

namespace Nethermind.Blockchain.Blocks;

/// <remarks>
/// Each value is the block RLP, then the RLP of the suggested block access list, then the RLP of the generated one.
/// Both access lists are optional. When only the generated one is kept, an empty RLP string marks the missing suggested one.
/// The access lists are returned as <see cref="Block.EncodedBlockAccessList"/> and
/// <see cref="Block.EncodedGeneratedBlockAccessList"/> and are not decoded.
/// An access list larger than <paramref name="maxBlockAccessListSize"/> is not kept. When the kept access lists are larger
/// than <paramref name="blockAccessListBudget"/> in total, the access lists of the oldest entries are removed and their blocks stay.
/// </remarks>
public class BadBlockStore(
    IDb blockDb,
    long maxSize,
    IHeaderDecoder? headerDecoder = null,
    int maxBlockAccessListSize = BadBlockStore.MaxBlockAccessListSize,
    long blockAccessListBudget = BadBlockStore.BlockAccessListBudget,
    ILogManager? logManager = null) : IBadBlockStore
{
    // EIP-7928 allows gas limit / ItemCost (2000) items. At about 64 bytes per item (a 32-byte slot and a 32-byte value
    // with RLP overhead), 16 MiB holds about 260k items, which is the limit of a block with a gas limit of about 500M.
    public const int MaxBlockAccessListSize = 16 * 1024 * 1024;

    // Each read of the store copies every kept access list, so 100 entries at the per-entry limit would be too much.
    public const long BlockAccessListBudget = 128 * 1024 * 1024;

    // An access list is an RLP sequence, so an empty RLP string cannot be one.
    private const byte NoBlockAccessList = 0x80;

    private readonly BlockDecoder _blockDecoder = new(headerDecoder ?? new HeaderDecoder());
    private readonly ILogger _logger = (logManager ?? NullLogManager.Instance).GetClassLogger<BadBlockStore>();
    private readonly Lock _lock = new();

    // Access list bytes per DB key. Loaded on the first insert that keeps an access list, so later inserts do not read every value.
    private Dictionary<byte[], int>? _blockAccessListSizes;
    private long _blockAccessListTotal;

    public void Insert(Block block, WriteFlags writeFlags = WriteFlags.None)
    {
        if (block.Hash is null)
        {
            throw new InvalidOperationException("An attempt to store a block with a null hash.");
        }

        using ArrayPoolSpan<byte> rlp = _blockDecoder.EncodeToArrayPoolSpan(block);
        byte[]? suggested = WithinLimit(block, "suggested", block.EncodedBlockAccessList
            ?? (block.BlockAccessList is { } blockAccessList ? BlockAccessListDecoder.EncodeToBytes(blockAccessList) : null));
        byte[]? generated = WithinLimit(block, "generated", block.EncodedGeneratedBlockAccessList
            ?? (block.GeneratedBlockAccessList is { } generatedBlockAccessList ? BlockAccessListDecoder.EncodeToBytes(generatedBlockAccessList) : null));
        int balSize = (suggested?.Length ?? 0) + (generated?.Length ?? 0);

        lock (_lock)
        {
            if (balSize == 0)
            {
                blockDb.Set(block.Number, block.Hash, rlp, writeFlags);
            }
            else
            {
                int suggestedLength = suggested?.Length ?? (generated is null ? 0 : 1);
                using ArrayPoolSpan<byte> value = new(rlp.Length + suggestedLength + (generated?.Length ?? 0));
                ((ReadOnlySpan<byte>)rlp).CopyTo(value);
                Span<byte> suggestedSpan = value.Slice(rlp.Length, suggestedLength);
                if (suggested is null) suggestedSpan[0] = NoBlockAccessList;
                else suggested.CopyTo(suggestedSpan);
                generated?.CopyTo(value.Slice(rlp.Length + suggestedLength, generated.Length));
                blockDb.Set(block.Number, block.Hash, value, writeFlags);
            }

            if (balSize > 0) LoadBlockAccessListSizes();
            if (_blockAccessListSizes is not null)
            {
                byte[] key = new byte[40];
                KeyValueStoreExtensions.GetBlockNumPrefixedKey(block.Number, block.Hash, key);
                SetBlockAccessListSize(key, balSize);
            }

            TruncateToMaxSize();
            if (balSize > 0) TrimBlockAccessLists(writeFlags);
        }
    }

    public IEnumerable<Block> GetAll() => blockDb.GetAllValues(true).Select(bytes =>
    {
        RlpReader ctx = new(((byte[]?)bytes ?? []));
        Block block = _blockDecoder.Decode(ref ctx);
        if (ctx.Position < ctx.Length)
        {
            if (ctx.Data[ctx.Position] == NoBlockAccessList)
            {
                ctx.Position++;
            }
            else
            {
                int length = ctx.PeekNextRlpLength();
                block.EncodedBlockAccessList = ctx.Data.Slice(ctx.Position, length).ToArray();
                ctx.Position += length;
            }

            if (ctx.Position < ctx.Length)
            {
                block.EncodedGeneratedBlockAccessList = ctx.Data[ctx.Position..].ToArray();
            }
        }

        return block;
    });

    private byte[]? WithinLimit(Block block, string kind, byte[]? bal)
    {
        if (bal is null || bal.Length <= maxBlockAccessListSize) return bal;

        if (_logger.IsDebug) _logger.Debug($"Bad block {block.ToString(Block.Format.Short)}: {kind} block access list of {bal.Length} bytes is over the limit of {maxBlockAccessListSize} bytes and is not kept.");
        return null;
    }

    private void LoadBlockAccessListSizes()
    {
        if (_blockAccessListSizes is not null) return;

        _blockAccessListSizes = new(Bytes.EqualityComparer);
        foreach (KeyValuePair<byte[], byte[]> entry in blockDb.GetAll(true))
        {
            SetBlockAccessListSize(entry.Key, entry.Value.Length - new RlpReader(entry.Value).PeekNextRlpLength());
        }
    }

    private void SetBlockAccessListSize(byte[] key, int size)
    {
        if (_blockAccessListSizes is null) return;

        if (_blockAccessListSizes.Remove(key, out int previous)) _blockAccessListTotal -= previous;
        if (size > 0)
        {
            _blockAccessListSizes[key] = size;
            _blockAccessListTotal += size;
        }
    }

    private void TruncateToMaxSize()
    {
        int toDelete = (int)(blockDb.EstimatedCount - maxSize);
        if (toDelete > 0)
        {
            // The key is the block number followed by the hash, so the values do not need to be decoded.
            foreach (byte[] key in blockDb.GetAllKeys(true))
            {
                if (toDelete-- == 0) break;
                blockDb.Remove(key);
                SetBlockAccessListSize(key, 0);
            }
        }
    }

    private void TrimBlockAccessLists(WriteFlags writeFlags)
    {
        if (_blockAccessListSizes is null || _blockAccessListTotal <= blockAccessListBudget) return;

        // Keys are ordered by block number, so the oldest blocks lose their access lists first.
        foreach (byte[] key in blockDb.GetAllKeys(true))
        {
            if (_blockAccessListTotal <= blockAccessListBudget) break;
            if (!_blockAccessListSizes.ContainsKey(key) || blockDb.Get(key) is not { } value) continue;

            blockDb.PutSpan(key, value.AsSpan(0, new RlpReader(value).PeekNextRlpLength()), writeFlags);
            SetBlockAccessListSize(key, 0);
            if (_logger.IsDebug) _logger.Debug($"Bad block store is over the access list budget of {blockAccessListBudget} bytes. Removed the access lists of the entry {key.ToHexString()}.");
        }
    }
}
