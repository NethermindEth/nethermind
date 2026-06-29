// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Merge.Plugin.SszRest.Handlers;

/// <summary>
/// Handles <c>POST /engine/v2/{fork}/payloads/witness</c> (EIP-7928). The SSZ request body is the
/// same shape as the standard <c>payloads</c> endpoint; the SSZ response is
/// <c>PayloadStatusWithWitness</c> — the payload status plus an optional witness that is present only
/// when the status is VALID (execution-apis#773/#793).
/// </summary>
public sealed class NewPayloadWithWitnessSszHandler(IEngineRpcModule engineModule) : SszEndpointHandlerBase
{
    public override string HttpMethod => "POST";

    public override string Resource => SszRestPaths.PayloadsWitness;

    // Version-less: the endpoint is gated by the fork segment (Amsterdam+), not a version number.
    public override int? Version => null;

    public override async Task HandleAsync(HttpContext ctx, int version, ReadOnlyMemory<char> extra, ReadOnlySequence<byte> body)
    {
        NewPayloadV5RequestWire.Decode(body, out NewPayloadV5RequestWire wire);
        ExecutionPayloadV4 ep = wire.ExecutionPayload.AsExecutionPayload();
        ResultWrapper<NewPayloadWithWitnessV1Result> result = await engineModule.engine_newPayloadWithWitness(
            ep, SszCodec.GetBlobVersionedHashes(ep), wire.ParentBeaconBlockRoot, wire.ExecutionRequests.ToExecutionRequests());
        await WriteSszResultAsync(ctx, result, EncodeResponse);
    }

    private static int EncodeResponse(NewPayloadWithWitnessV1Result result, IBufferWriter<byte> writer)
    {
        PayloadStatusV1 status = new()
        {
            Status = result.Status,
            LatestValidHash = result.LatestValidHash,
            ValidationError = result.ValidationError
        };
        return SszCodec.EncodeNewPayloadWithWitnessResponse(status, result.ExecutionWitness, writer);
    }
}
