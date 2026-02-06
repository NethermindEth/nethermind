// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Int256;
using Nethermind.JsonRpc;

namespace Nethermind.Taiko.Rpc;

public class TaikoExtendedEthModule(
    ISyncConfig syncConfig,
    IL1OriginStore l1OriginStore) : ITaikoExtendedEthRpcModule
{
    internal static readonly ResultWrapper<L1Origin?> L1OriginNotFound = ResultWrapper<L1Origin?>.Fail("not found");
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
            return L1OriginNotFound;
        }

        L1Origin? origin = l1OriginStore.ReadL1Origin(blockId.Value);

        return origin is null ? L1OriginNotFound : ResultWrapper<L1Origin?>.Success(origin);
    }

    public Task<ResultWrapper<UInt256?>> taiko_lastBlockIDByBatchID(UInt256 batchId)
    {
        UInt256? blockId = l1OriginStore.ReadBatchToLastBlockID(batchId);
        return blockId is null ? BlockIdNotFound : ResultWrapper<UInt256?>.Success(blockId);
    }
}
