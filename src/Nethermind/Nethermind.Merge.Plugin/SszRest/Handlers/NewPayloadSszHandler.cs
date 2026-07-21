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
/// Handles <c>POST /engine/v2/payloads</c>, the SSZ-REST equivalent of
/// <c>engine_newPayloadV{N}</c> (the version is selected by the <c>Eth-Execution-Version</c>
/// header). Generic over a per-version descriptor so adding V6 is one new descriptor struct +
/// one DI line — no version switch.
/// </summary>
public sealed class NewPayloadSszHandler<TVersion, TWire>(IEngineRpcModule engineModule) : SszEndpointHandlerBase
    where TVersion : struct, INewPayloadVersion<TWire>
    where TWire : struct, ISszCodec<TWire>
{
    public override string HttpMethod => "POST";
    public override string Resource => SszRestPaths.Payloads;
    public override int? Version => TVersion.VersionNumber;

    public override async Task HandleAsync(HttpContext ctx, int version, ReadOnlyMemory<char> extra, ReadOnlySequence<byte> body)
    {
        TWire.Decode(body, out TWire wire);
        ResultWrapper<PayloadStatusV1> result = await TVersion.Call(engineModule, wire);
        await WriteSszResultAsync(ctx, result, SszCodec.EncodePayloadStatus);
    }
}
