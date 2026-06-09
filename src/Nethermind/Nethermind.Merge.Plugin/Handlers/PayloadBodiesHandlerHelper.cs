// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
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

    public static PayloadBodyV1? CreatePayloadBodyV1(IBlockStore blockStore, BlockHeader? header, Hash256 blockHash) =>
        header is null ? null : CreatePayloadBodyV1(blockStore, header.Number, blockHash);

    public static PayloadBodyV1? CreatePayloadBodyV1(IBlockStore blockStore, BlockHeader? header) =>
        header?.Hash is { } blockHash ? CreatePayloadBodyV1(blockStore, header.Number, blockHash) : null;

    public static PayloadBodyV2? CreatePayloadBodyV2(IBlockStore blockStore, IBlockAccessListStore balStore, BlockHeader? header, Hash256 blockHash) =>
        header is null ? null : CreatePayloadBodyV2(blockStore, balStore, header.Number, blockHash);

    public static PayloadBodyV2? CreatePayloadBodyV2(IBlockStore blockStore, IBlockAccessListStore balStore, BlockHeader? header) =>
        header?.Hash is { } blockHash ? CreatePayloadBodyV2(blockStore, balStore, header.Number, blockHash) : null;

    private static PayloadBodyV1? CreatePayloadBodyV1(IBlockStore blockStore, long blockNumber, Hash256 blockHash)
    {
        byte[]? blockRlp = blockStore.GetRlp(blockNumber, blockHash);
        return blockRlp is null ? null : PayloadBodiesV1DirectResponse.CreatePayloadBody(blockRlp);
    }

    private static PayloadBodyV2? CreatePayloadBodyV2(IBlockStore blockStore, IBlockAccessListStore balStore, long blockNumber, Hash256 blockHash)
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
