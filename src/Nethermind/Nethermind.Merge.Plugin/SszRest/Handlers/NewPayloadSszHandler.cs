// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Nethermind.Consensus;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Merge.Plugin.SszRest.Handlers;

/// <summary>
/// Handles <c>POST /engine/v{N}/payloads</c>, the SSZ-REST equivalent of
/// <c>engine_newPayloadV{N}</c>.
/// </summary>
public sealed class NewPayloadSszHandler(IEngineRpcModule engineModule) : SszEndpointHandlerBase
{
    private readonly IEngineRpcModule _engineModule = engineModule;

    public override string HttpMethod => "POST";
    public override string Resource => "payloads";

    public override async Task HandleAsync(HttpContext ctx, int version, string extra, ReadOnlyMemory<byte> body)
    {
        (ExecutionPayload payload, byte[]?[] versionedHashes, Hash256? beaconRoot, byte[][]? requests) =
            SszCodec.DecodeNewPayloadRequest(body.Span, version);

        ResultWrapper<PayloadStatusV1> result = version switch
        {
            <= EngineApiVersions.NewPayload.V1 => await _engineModule.engine_newPayloadV1(payload),
            EngineApiVersions.NewPayload.V2 => await _engineModule.engine_newPayloadV2(payload),
            EngineApiVersions.NewPayload.V3 => await _engineModule.engine_newPayloadV3(
                (ExecutionPayloadV3)payload, versionedHashes, beaconRoot),
            EngineApiVersions.NewPayload.V4 => await _engineModule.engine_newPayloadV4(
                (ExecutionPayloadV3)payload, versionedHashes, beaconRoot, requests),
            _ => await _engineModule.engine_newPayloadV5(
                (ExecutionPayloadV4)payload, versionedHashes, beaconRoot, requests),
        };

        await WriteSszResultAsync(ctx, result, SszCodec.EncodePayloadStatus);
    }
}
