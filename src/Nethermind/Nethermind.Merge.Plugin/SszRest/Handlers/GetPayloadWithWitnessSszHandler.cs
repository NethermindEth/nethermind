// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Nethermind.Consensus.Stateless;
using Nethermind.Core;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;

namespace Nethermind.Merge.Plugin.SszRest.Handlers;

/// <summary>
/// Handles <c>GET /engine/v1/payloads/{payload_id}/witness</c> (execution-apis#917): the payload that
/// <c>GET /payloads/{payload_id}</c> returns, together with its execution witness.
/// </summary>
/// <remarks>
/// The witness is built on demand by re-executing the returned snapshot on its parent state, so it covers only the
/// included transactions and equals the one <c>POST /payloads/witness</c> returns for that payload.
/// </remarks>
public sealed class GetPayloadWithWitnessSszHandler(IEngineRpcModule engine, BlockWitnessGenerator witnessGenerator)
    : SszEndpointHandlerBase
{
    private const int PayloadIdByteLength = 8;

    public override string HttpMethod => "GET";
    public override string Resource => SszRestPaths.BuiltPayloadWithWitness;
    public override int? Version => EngineApiVersions.GetPayload.V6;
    public override bool AcceptsPathExtra => true;

    public override async Task HandleAsync(HttpContext ctx, int version, ReadOnlyMemory<char> extra, ReadOnlySequence<byte> body)
    {
        ctx.Response.Headers.CacheControl = "no-store";

        byte[] payloadId = new byte[PayloadIdByteLength];
        if (!TryDecodeHexPathExtra(extra.Span, payloadId))
        {
            await WriteErrorAsync(ctx, StatusCodes.Status400BadRequest, $"Invalid payload ID: '{extra}'");
            return;
        }

        ResultWrapper<GetPayloadV6Result?> payload = await engine.engine_getPayloadV6(payloadId);
        if (payload.Data is not { } builtPayload)
        {
            await WriteSszResultAsync(ctx, payload, SszCodec.EncodeGetPayloadV6Response);
            return;
        }

        using Witness? witness = witnessGenerator.Generate(builtPayload.Block);
        if (witness is null)
        {
            payload.Dispose();
            await WriteErrorAsync(ctx, StatusCodes.Status500InternalServerError,
                $"Cannot generate the witness of payload {builtPayload.Block.Hash}: its parent state is not available");
            return;
        }

        await WriteSszResultAsync(ctx, payload, (result, writer) => SszCodec.EncodeGetPayloadWithWitnessV6Response(result!, witness, writer));
    }
}
