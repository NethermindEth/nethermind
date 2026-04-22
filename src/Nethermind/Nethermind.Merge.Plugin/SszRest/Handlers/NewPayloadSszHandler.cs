// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;

namespace Nethermind.Merge.Plugin.SszRest.Handlers;

/// <summary>
/// Handles <c>POST /engine/v{N}/payloads</c>, the SSZ-REST equivalent of
/// <c>engine_newPayloadV{N}</c>.
/// </summary>
public sealed class NewPayloadSszHandler(IAsyncHandler<ExecutionPayload, PayloadStatusV1> handler) : SszEndpointHandlerBase
{
    private readonly IAsyncHandler<ExecutionPayload, PayloadStatusV1> _handler = handler;

    public override string HttpMethod => "POST";
    public override string Resource => "payloads";

    public override async Task HandleAsync(HttpContext ctx, int version, string extra, byte[] body)
    {
        (ExecutionPayload payload, byte[]?[] _, Hash256? beaconRoot, byte[][]? requests) =
            SszCodec.DecodeNewPayloadRequest(body, version);

        if (version >= 3)
            payload.ParentBeaconBlockRoot = beaconRoot;

        payload.ExecutionRequests = requests;

        ResultWrapper<PayloadStatusV1> result = await _handler.HandleAsync(payload);
        await WriteSszResultAsync(ctx, result, SszCodec.EncodePayloadStatus);
    }
}
