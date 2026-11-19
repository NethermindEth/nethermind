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

    public static PayloadBodyV1? CreatePayloadBodyV1(IBlockStore blockStore, IStarkProofStore proofStore, BlockHeader? header, Hash256 blockHash) =>
        header is null ? null : CreatePayloadBodyV1(blockStore, proofStore, header, header.Number, blockHash);

    public static PayloadBodyV1? CreatePayloadBodyV1(IBlockStore blockStore, IStarkProofStore proofStore, BlockHeader? header) =>
        header?.Hash is { } blockHash ? CreatePayloadBodyV1(blockStore, proofStore, header, header.Number, blockHash) : null;

    public static PayloadBodyV2? CreatePayloadBodyV2(IBlockStore blockStore, IBlockAccessListStore balStore, IStarkProofStore proofStore, BlockHeader? header, Hash256 blockHash) =>
        header is null ? null : CreatePayloadBodyV2(blockStore, balStore, proofStore, header, header.Number, blockHash);

    public static PayloadBodyV2? CreatePayloadBodyV2(IBlockStore blockStore, IBlockAccessListStore balStore, IStarkProofStore proofStore, BlockHeader? header) =>
        header?.Hash is { } blockHash ? CreatePayloadBodyV2(blockStore, balStore, proofStore, header, header.Number, blockHash) : null;

    private static PayloadBodyV1? CreatePayloadBodyV1(IBlockStore blockStore, IStarkProofStore proofStore, BlockHeader header, ulong blockNumber, Hash256 blockHash)
    {
        byte[]? blockRlp = blockStore.GetRlp(blockNumber, blockHash);
        return blockRlp is null || !TryGetStarkProof(proofStore, header, blockNumber, blockHash, out byte[]? starkProof)
            ? null
            : PayloadBodiesV1DirectResponse.CreatePayloadBody(blockRlp, starkProof);
    }

    private static PayloadBodyV2? CreatePayloadBodyV2(IBlockStore blockStore, IBlockAccessListStore balStore, IStarkProofStore proofStore, BlockHeader header, ulong blockNumber, Hash256 blockHash)
    {
        byte[]? blockRlp = blockStore.GetRlp(blockNumber, blockHash);
        if (blockRlp is null || !TryGetStarkProof(proofStore, header, blockNumber, blockHash, out byte[]? starkProof))
        {
            return null;
        }

        MemoryManager<byte>? blockAccessList = balStore.GetRlp(blockNumber, blockHash);
        return PayloadBodiesV2DirectResponse.CreatePayloadBody(blockRlp, blockAccessList, starkProof);
    }

    /// <summary>Reads the EIP-8288 proof the block store keeps apart from the body.</summary>
    /// <returns><c>false</c> when the header commits to a proof that is not stored, so the body is not available.</returns>
    private static bool TryGetStarkProof(IStarkProofStore proofStore, BlockHeader header, ulong blockNumber, Hash256 blockHash, out byte[]? starkProof)
    {
        starkProof = header.RecursiveStark is null ? null : proofStore.Get(blockNumber, blockHash);
        return header.RecursiveStark is null || starkProof is not null;
    }
}
