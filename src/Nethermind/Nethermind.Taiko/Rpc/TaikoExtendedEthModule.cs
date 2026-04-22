// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using static Nethermind.Taiko.TaikoBlockValidator;

namespace Nethermind.Taiko.Rpc;

public class TaikoExtendedEthModule(
    ISyncConfig syncConfig,
    IL1OriginStore l1OriginStore,
    IBlockFinder blockFinder) : ITaikoExtendedEthRpcModule
{
    internal static readonly ResultWrapper<L1Origin?> L1OriginNotFound = ResultWrapper<L1Origin?>.Fail("not found");
    private static readonly ResultWrapper<UInt256?> BlockIdNotFound = ResultWrapper<UInt256?>.Fail("not found");

    /// <summary>
    /// Maximum number of blocks to scan backwards when the batch→block index is missing.
    /// Matches alethia-reth's <c>MAX_BACKWARD_SCAN_BLOCKS = 192 * 21_600</c>.
    /// </summary>
    private const int MaxBatchLookupBlocks = 192 * 21_600;

    public Task<ResultWrapper<string>> taiko_getSyncMode() => ResultWrapper<string>.Success(syncConfig switch
    {
        { SnapSync: true } => "snap",
        _ => "full",
    });

    public Task<ResultWrapper<L1Origin?>> taiko_headL1Origin()
    {
        UInt256? head = l1OriginStore.ReadHeadL1Origin();
        if (head is null)
        {
            return L1OriginNotFound;
        }

        L1Origin? origin = l1OriginStore.ReadL1Origin(head.Value);

        return origin is null ? L1OriginNotFound : ResultWrapper<L1Origin?>.Success(origin);
    }

    public Task<ResultWrapper<L1Origin?>> taiko_l1OriginByID(UInt256 blockId)
    {
        L1Origin? origin = l1OriginStore.ReadL1Origin(blockId);

        return origin is null ? L1OriginNotFound : ResultWrapper<L1Origin?>.Success(origin);
    }

    public Task<ResultWrapper<L1Origin?>> taiko_lastL1OriginByBatchID(UInt256 batchId)
    {
        UInt256? blockId = l1OriginStore.ReadBatchToLastBlockID(batchId);
        if (blockId is null)
        {
            blockId = GetLastBlockByBatchId(batchId);
            if (blockId is null)
            {
                return L1OriginNotFound;
            }
        }

        L1Origin? origin = l1OriginStore.ReadL1Origin(blockId.Value);

        return origin is null ? L1OriginNotFound : ResultWrapper<L1Origin?>.Success(origin);
    }

    public Task<ResultWrapper<UInt256?>> taiko_lastBlockIDByBatchID(UInt256 batchId)
    {
        UInt256? blockId = l1OriginStore.ReadBatchToLastBlockID(batchId);
        if (blockId is null)
        {
            blockId = GetLastBlockByBatchId(batchId);
            if (blockId is null)
            {
                return BlockIdNotFound;
            }
        }

        return ResultWrapper<UInt256?>.Success(blockId);
    }

    /// <summary>
    /// Scans backwards from head to find the last block belonging to <paramref name="batchId"/>.
    /// Used as a fallback when the batch→block index has not been populated (e.g. legacy nodes
    /// or nodes that were upgraded without replaying historical blocks). Capped at
    /// <see cref="MaxBatchLookupBlocks"/> iterations to prevent unbounded RPC thread blocking.
    /// </summary>
    private UInt256? GetLastBlockByBatchId(UInt256 batchId)
    {
        Block? currentBlock = blockFinder.Head;
        int scanned = 0;

        while (currentBlock is not null &&
               currentBlock.Transactions.Length > 0 &&
               HasAnchorV4Prefix(currentBlock.Transactions[0].Data))
        {
            if (currentBlock.Number == 0)
                break;

            if (scanned >= MaxBatchLookupBlocks)
                return null;

            scanned++;

            // Skip preconfirmation blocks (no L1 origin entry or L1 block height == 0).
            L1Origin? l1Origin = l1OriginStore.ReadL1Origin((UInt256)currentBlock.Number);
            if (l1Origin is not null && l1Origin.IsPreconfBlock)
            {
                currentBlock = blockFinder.FindBlock(currentBlock.Number - 1);
                continue;
            }

            UInt256? proposalId = currentBlock.Header.DecodeShastaProposalID();
            if (proposalId is null)
                return null;

            if (proposalId.Value == batchId)
                return (UInt256)currentBlock.Number;

            currentBlock = blockFinder.FindBlock(currentBlock.Number - 1);
        }

        return null;
    }

    private static bool HasAnchorV4Prefix(ReadOnlyMemory<byte> data) =>
        data.Length >= 4 && (AnchorV4Selector.AsSpan().SequenceEqual(data.Span[..4])
            || AnchorV4WithSignalSlotsSelector.AsSpan().SequenceEqual(data.Span[..4]));
}
