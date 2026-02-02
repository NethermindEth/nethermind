// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Consensus;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;

namespace Nethermind.Merge.Plugin;

public partial class EngineRpcModule : IEngineRpcModule
{
    private readonly IAsyncHandler<byte[], GetPayloadV6Result?> _getPayloadHandlerV6;
    private readonly IAsyncHandler<Hash256[], IEnumerable<byte[]?>> _getBALSByHashV1Handler;
    private readonly IAsyncHandler<(long, long), IEnumerable<byte[]>?> _getBALSByRangeV1Handler;

    public Task<ResultWrapper<GetPayloadV6Result?>> engine_getPayloadV6(byte[] payloadId)
        => _getPayloadHandlerV6.HandleAsync(payloadId);

    public Task<ResultWrapper<PayloadStatusV1>> engine_newPayloadV5(ExecutionPayloadV4 executionPayload, byte[]?[] blobVersionedHashes, Hash256? parentBeaconBlockRoot, byte[][]? executionRequests)
        => NewPayload(new ExecutionPayloadParams<ExecutionPayloadV4>(executionPayload, blobVersionedHashes, parentBeaconBlockRoot, executionRequests), EngineApiVersions.Amsterdam);

    public Task<ResultWrapper<IEnumerable<byte[]?>>> engine_getBALSByHashV1(Hash256[] blockHashes)
        => _getBALSByHashV1Handler.HandleAsync(blockHashes);

    public Task<ResultWrapper<IEnumerable<byte[]>?>> engine_getBALSByRangeV1(long start, long count)
        => _getBALSByRangeV1Handler.HandleAsync((start, count));
}
