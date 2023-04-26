// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;

namespace Nethermind.Merge.Plugin;

public partial class EngineRpcModule : IEngineRpcModule
{
    private readonly IAsyncHandler<byte[], GetPayloadV3Result?> _getPayloadHandlerV3;

    public Task<ResultWrapper<PayloadStatusV1>> engine_newPayloadV3(ExecutionPayload executionPayload) =>
        NewPayload(executionPayload, 3);

    public async Task<ResultWrapper<GetPayloadV3Result?>> engine_getPayloadV3(byte[] payloadId) =>
        await _getPayloadHandlerV3.HandleAsync(payloadId);
}
