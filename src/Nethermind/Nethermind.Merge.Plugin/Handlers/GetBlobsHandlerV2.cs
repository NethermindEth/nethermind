// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Facade.Eth;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.TxPool;

namespace Nethermind.Merge.Plugin.Handlers;

public class GetBlobsHandlerV2(ITxPool txPool, IEthSyncingInfo? ethSyncingInfo) : IAsyncHandler<GetBlobsHandlerV2Request, IReadOnlyList<BlobAndProofV2?>?>
{
    private const int MaxRequest = 128;

    private static readonly Task<ResultWrapper<IReadOnlyList<BlobAndProofV2?>?>> NotFound = Task.FromResult(ResultWrapper<IReadOnlyList<BlobAndProofV2?>?>.Success(null));

    public GetBlobsHandlerV2(ITxPool txPool)
        : this(txPool, null)
    {
    }

    public Task<ResultWrapper<IReadOnlyList<BlobAndProofV2?>?>> HandleAsync(GetBlobsHandlerV2Request request)
    {
        if (request.BlobVersionedHashes.Length > MaxRequest)
        {
            string error = $"The number of requested blobs must not exceed {MaxRequest}";
            return ResultWrapper<IReadOnlyList<BlobAndProofV2?>?>.Fail(error, MergeErrorCodes.TooLargeRequest);
        }

        Metrics.GetBlobsRequestsTotal += request.BlobVersionedHashes.Length;

        if (IsBehindHead())
        {
            return ReturnEmptyArray();
        }

        int n = request.BlobVersionedHashes.Length;
        if (n == 0)
        {
            Metrics.GetBlobsRequestsSuccessTotal++;
            return ResultWrapper<IReadOnlyList<BlobAndProofV2?>?>.Success(Array.Empty<BlobAndProofV2?>());
        }

        ArrayPoolList<byte[]?> blobs = new(n, n);
        ArrayPoolList<ReadOnlyMemory<byte[]>> proofs = new(n, n);
        try
        {
            int count = txPool.TryGetBlobsAndProofsV1(request.BlobVersionedHashes, blobs.AsSpan(), proofs.AsSpan());

            Metrics.GetBlobsRequestsInBlobpoolTotal += count;

            // quick fail if we don't have some blob (unless partial return is allowed)
            if (!request.AllowPartialReturn && count != n)
            {
                blobs.Dispose();
                proofs.Dispose();
                return ReturnEmptyArray();
            }

            Metrics.GetBlobsRequestsSuccessTotal++;
            return ResultWrapper<IReadOnlyList<BlobAndProofV2?>?>.Success(new BlobsV2DirectResponse(blobs, proofs, n));
        }
        catch
        {
            blobs.Dispose();
            proofs.Dispose();
            throw;
        }
    }

    /// <summary>Whether the node has not reached the chain head yet, so the spec requires a <c>null</c> response.</summary>
    /// <remarks>
    /// <see cref="IEthSyncingInfo.IsSyncing"/> alone also holds while old bodies and receipts download after state sync,
    /// when the node already follows the head and its pool takes gossip (<see cref="SyncMode.WaitingForBlock"/>, see <see cref="SyncedTxGossipPolicy"/>).
    /// </remarks>
    private bool IsBehindHead() =>
        ethSyncingInfo is not null && (ethSyncingInfo.SyncMode & SyncMode.WaitingForBlock) == 0 && ethSyncingInfo.IsSyncing();

    private Task<ResultWrapper<IReadOnlyList<BlobAndProofV2?>?>> ReturnEmptyArray()
    {
        Metrics.GetBlobsRequestsFailureTotal++;
        return NotFound;
    }
}

public readonly record struct GetBlobsHandlerV2Request(byte[][] BlobVersionedHashes, bool AllowPartialReturn = false);
