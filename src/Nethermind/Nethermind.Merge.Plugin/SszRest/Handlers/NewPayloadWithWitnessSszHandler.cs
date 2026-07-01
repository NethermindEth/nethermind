// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Serialization.Ssz;

namespace Nethermind.Merge.Plugin.SszRest.Handlers;

/// <summary>
/// Handles <c>POST /engine/v2/{fork}/payloads/witness</c> (EIP-7928), the SSZ-REST equivalent of
/// <c>engine_newPayloadWithWitness</c>. Generic over a per-version descriptor so a new version is one
/// new descriptor struct + one DI line — mirroring <see cref="NewPayloadSszHandler{TVersion,TWire}"/>.
/// The SSZ request body matches the standard <c>payloads</c> endpoint; the SSZ response is
/// <c>PayloadStatusWithWitness</c> — the payload status plus a witness present only when the status is
/// VALID (execution-apis#773/#793).
/// </summary>
public sealed class NewPayloadWithWitnessSszHandler<TVersion, TWire>(IEngineRpcModule engineModule) : SszEndpointHandlerBase
    where TVersion : struct, INewPayloadWithWitnessVersion<TWire>
    where TWire : struct, ISszCodec<TWire>
{
    public override string HttpMethod => "POST";
    public override string Resource => SszRestPaths.PayloadWithWitness;
    public override int? Version => TVersion.VersionNumber;

    public override async Task HandleAsync(HttpContext ctx, int version, ReadOnlyMemory<char> extra, ReadOnlySequence<byte> body)
    {
        TWire.Decode(body, out TWire wire);
        ResultWrapper<NewPayloadWithWitnessV1Result> result = await TVersion.Call(engineModule, wire);
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
