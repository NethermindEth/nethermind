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
    private static readonly ResultWrapper<L1Origin?> L1OriginNotFound = ResultWrapper<L1Origin?>.Fail("not found");
    private static readonly ResultWrapper<UInt256?> BlockIdNotFound = ResultWrapper<UInt256?>.Fail("not found");

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
    /// Traverses the blockchain backwards to find the last Shasta block of the given batch ID.
    /// </summary>
    /// <param name="batchId">The Shasta batch identifier for which to find the last corresponding block.</param>
    private UInt256? GetLastBlockByBatchId(UInt256 batchId)
    {
        Block? currentBlock = blockFinder.Head;

        while (currentBlock is not null &&
               currentBlock.Transactions.Length > 0 &&
               HasAnchorV4Prefix(currentBlock.Transactions[0].Data))
        {
            if (currentBlock.Number == 0)
            {
                break;
            }

            UInt256? proposalId = currentBlock.Header.DecodeShastaProposalID();
            if (proposalId is null)
            {
                return null;
            }

            if (proposalId.Value == batchId)
            {
                return (UInt256)currentBlock.Number;
            }

            currentBlock = blockFinder.FindBlock(currentBlock.Number - 1);
        }

        return null;
    }

    private static bool HasAnchorV4Prefix(ReadOnlyMemory<byte> data)
    {
        return data.Length >= 4 && (AnchorV4Selector.AsSpan().SequenceEqual(data.Span[..4])
            || AnchorV4WithSignalSlotsSelector.AsSpan().SequenceEqual(data.Span[..4]));
    }
}
