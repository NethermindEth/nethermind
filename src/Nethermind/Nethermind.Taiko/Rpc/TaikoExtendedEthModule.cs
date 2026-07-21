// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Int256;
using Nethermind.JsonRpc;

namespace Nethermind.Taiko.Rpc;

/// <summary>
/// Implements the public <c>taiko_</c> JSON-RPC namespace. Batch-lookup helpers
/// (<c>taiko_lastL1OriginByBatchID</c> / <c>taiko_lastBlockIDByBatchID</c>) intentionally
/// live only on the engine/auth namespace (<see cref="TaikoEngineRpcModule"/>) — matching
/// the alethia-reth reference shape — to avoid two divergent code paths.
/// </summary>
public class TaikoExtendedEthModule(
    ISyncConfig syncConfig,
    IL1OriginStore l1OriginStore) : ITaikoExtendedEthRpcModule
{
    /// <summary>
    /// Cached "not found" result for L1-origin lookups.
    /// Uses ResourceNotFound (-32000) instead of the default InternalError (-32603), and
    /// IsTemporary so the JsonRpc framework's SuppressWarning flag fires
    /// (JsonRpcService.cs:158 -> JsonRpcProcessor.cs:428). Without this, every cold-boot
    /// taiko_headL1Origin / taiko_l1OriginByID poll on a node that hasn't yet seen any L1
    /// batches produces a loud "Error response handling JsonRpc..." WARN line on a
    /// known-transient miss.
    /// </summary>
    internal static readonly ResultWrapper<L1Origin?> L1OriginNotFound =
        ResultWrapper<L1Origin?>.Fail("not found", ErrorCodes.ResourceNotFound, isTemporary: true);

    /// <inheritdoc />
    public Task<ResultWrapper<string>> taiko_getSyncMode() => ResultWrapper<string>.Success(syncConfig switch
    {
        { SnapSync: true } => "snap",
        _ => "full",
    });

    /// <inheritdoc />
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

    /// <inheritdoc />
    public Task<ResultWrapper<L1Origin?>> taiko_l1OriginByID(UInt256 blockId)
    {
        L1Origin? origin = l1OriginStore.ReadL1Origin(blockId);

        return origin is null ? L1OriginNotFound : ResultWrapper<L1Origin?>.Success(origin);
    }
}
