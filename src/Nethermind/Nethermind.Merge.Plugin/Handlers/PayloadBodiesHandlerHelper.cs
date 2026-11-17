// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Blockchain.BlockAccessLists;
using Nethermind.Blockchain.Blocks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Merge.Plugin.Data;
using PayloadBodyV1 = Nethermind.Merge.Plugin.Data.PayloadBodiesV1DirectResponse.PayloadBody;
using PayloadBodyV2 = Nethermind.Merge.Plugin.Data.PayloadBodiesV2DirectResponse.PayloadBody;

namespace Nethermind.Merge.Plugin.Handlers;

internal static class PayloadBodiesHandlerHelper
{
    public const int MaxCount = 1024;
    public const BlockTreeLookupOptions HashLookupOptions =
        BlockTreeLookupOptions.TotalDifficultyNotNeeded |
        BlockTreeLookupOptions.DoNotCreateLevelIfMissing;
    public const BlockTreeLookupOptions RangeLookupOptions =
        BlockTreeLookupOptions.RequireCanonical |
        HashLookupOptions;

    public static BlockHeader?[] FindHeaders(IBlockTree blockTree, ulong start, ulong end)
    {
        BlockHeader?[] headers = new BlockHeader?[end - start + 1];
        if (headers.Length > 1) blockTree.PrefetchCanonicalHeaders(start, headers.Length);
        for (ulong i = start; i <= end; i++) headers[i - start] = blockTree.FindHeader(i, RangeLookupOptions);
        return headers;
    }

    public static BlockHeader?[] FindHeaders(IBlockTree blockTree, IReadOnlyList<Hash256> blockHashes)
    {
        BlockHeader?[] headers = new BlockHeader?[blockHashes.Count];
        if (headers.Length > 1) blockTree.PrefetchHeaders(blockHashes);
        for (int i = 0; i < headers.Length; i++) headers[i] = blockTree.FindHeader(blockHashes[i], HashLookupOptions);
        return headers;
    }

    // Bodies are read ahead a window at a time, so that the blocks read ahead still fit the block cache when they are read.
    private const int PrefetchWindow = 32;

    /// <summary>Starts reading the bodies of a window of <paramref name="headers"/> when <paramref name="index"/> starts one.</summary>
    public static void PrefetchBodies(IBlockStore blockStore, ReadOnlySpan<BlockHeader?> headers, int index)
    {
        if (index % PrefetchWindow != 0 || headers.Length - index < 2) return;

        Span<(ulong Number, ValueHash256 Hash)> blocks = stackalloc (ulong, ValueHash256)[PrefetchWindow];
        int count = 0;
        foreach (BlockHeader? header in headers.Slice(index, Math.Min(PrefetchWindow, headers.Length - index)))
        {
            if (header?.Hash is { } hash) blocks[count++] = (header.Number, hash.ValueHash256);
        }

        blockStore.Prefetch(blocks[..count]);
    }

    public static PayloadBodyV1? CreatePayloadBodyV1(IBlockStore blockStore, BlockHeader? header, Hash256 blockHash) =>
        header is null ? null : CreatePayloadBodyV1(blockStore, header.Number, blockHash);

    public static PayloadBodyV1? CreatePayloadBodyV1(IBlockStore blockStore, BlockHeader? header) =>
        header?.Hash is { } blockHash ? CreatePayloadBodyV1(blockStore, header.Number, blockHash) : null;

    public static PayloadBodyV2? CreatePayloadBodyV2(IBlockStore blockStore, IBlockAccessListStore balStore, BlockHeader? header, Hash256 blockHash) =>
        header is null ? null : CreatePayloadBodyV2(blockStore, balStore, header.Number, blockHash);

    public static PayloadBodyV2? CreatePayloadBodyV2(IBlockStore blockStore, IBlockAccessListStore balStore, BlockHeader? header) =>
        header?.Hash is { } blockHash ? CreatePayloadBodyV2(blockStore, balStore, header.Number, blockHash) : null;

    private static PayloadBodyV1? CreatePayloadBodyV1(IBlockStore blockStore, ulong blockNumber, Hash256 blockHash)
    {
        byte[]? blockRlp = blockStore.GetRlp(blockNumber, blockHash);
        return blockRlp is null ? null : PayloadBodiesV1DirectResponse.CreatePayloadBody(blockRlp);
    }

    private static PayloadBodyV2? CreatePayloadBodyV2(IBlockStore blockStore, IBlockAccessListStore balStore, ulong blockNumber, Hash256 blockHash)
    {
        byte[]? blockRlp = blockStore.GetRlp(blockNumber, blockHash);
        if (blockRlp is null)
        {
            return null;
        }

        MemoryManager<byte>? blockAccessList = balStore.GetRlp(blockNumber, blockHash);
        return PayloadBodiesV2DirectResponse.CreatePayloadBody(blockRlp, blockAccessList);
    }
}
