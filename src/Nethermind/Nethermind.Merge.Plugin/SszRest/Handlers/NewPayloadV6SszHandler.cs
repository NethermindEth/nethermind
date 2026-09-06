// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Nethermind.Core;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Merge.Plugin.SszRest.Handlers;

/// <summary>SSZ-REST equivalent of <c>engine_newPayloadV6</c>.</summary>
/// <remarks>Dedicated rather than a <see cref="NewPayloadSszHandler{TVersion,TWire}"/> descriptor because
/// V6 alone returns <see cref="PayloadStatusV2"/>.</remarks>
public sealed class NewPayloadV6SszHandler(IEngineRpcModule engineModule) : SszEndpointHandlerBase
{
    public override string HttpMethod => "POST";
    public override string Resource => SszRestPaths.Payloads;
    public override int? Version => EngineApiVersions.NewPayload.V6;

    public override async Task HandleAsync(HttpContext ctx, int version, ReadOnlyMemory<char> extra, ReadOnlySequence<byte> body)
    {
        NewPayloadV6RequestWire.Decode(body, out NewPayloadV6RequestWire wire);
        ExecutionPayloadV4 ep = wire.ExecutionPayload.AsExecutionPayload();
        ResultWrapper<PayloadStatusV2> result = await engineModule.engine_newPayloadV6(
            ep,
            SszCodec.GetBlobVersionedHashes(ep),
            wire.ParentBeaconBlockRoot,
            wire.ExecutionRequests.ToExecutionRequests(),
            wire.InclusionListTransactions.ToExecutionRequests());
        await WriteSszResultAsync(ctx, result, SszCodec.EncodePayloadStatusV2);
    }
}
