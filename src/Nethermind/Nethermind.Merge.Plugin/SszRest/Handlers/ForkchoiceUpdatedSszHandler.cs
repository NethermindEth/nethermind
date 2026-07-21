// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Nethermind.Core.Specs;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Serialization.Ssz;

namespace Nethermind.Merge.Plugin.SszRest.Handlers;

/// <summary>
/// Handles <c>POST /engine/v2/forkchoice</c>, the SSZ-REST equivalent of
/// <c>engine_forkchoiceUpdatedV{N}</c> (the version is selected by the <c>Eth-Execution-Version</c>
/// header). Generic over a per-version descriptor so adding V5 is one new descriptor struct + one
/// DI line — no version switch.
/// </summary>
public sealed class ForkchoiceUpdatedSszHandler<TVersion, TWire>(IEngineRpcModule engineModule, ISpecProvider specProvider) : SszEndpointHandlerBase
    where TVersion : struct, IForkchoiceUpdatedVersion<TWire>
    where TWire : struct, ISszCodec<TWire>
{
    public override string HttpMethod => "POST";
    public override string Resource => SszRestPaths.Forkchoice;
    public override int? Version => TVersion.VersionNumber;

    public override async Task HandleAsync(HttpContext ctx, int version, ReadOnlyMemory<char> extra, ReadOnlySequence<byte> body)
    {
        TWire.Decode(body, out TWire wire);

        if (GetForkMismatchMessage(ctx, TVersion.GetTimestamp(wire)) is { } mismatch)
        {
            await WriteErrorAsync(ctx, StatusCodes.Status400BadRequest, mismatch, MergeErrorCodes.UnsupportedFork);
            return;
        }

        ResultWrapper<ForkchoiceUpdatedV1Result> result = await TVersion.Call(engineModule, wire);
        await WriteSszResultAsync(ctx, result, SszCodec.EncodeForkchoiceUpdatedResponse);
    }

    /// <summary>
    /// Returns an error message if the payload's timestamp fork doesn't match the fork requested via
    /// the <c>Eth-Execution-Version</c> header, or <c>null</c> when the check doesn't apply (no
    /// payload attributes / no route fork).
    /// </summary>
    private string? GetForkMismatchMessage(HttpContext ctx, ulong? timestamp)
    {
        if (!timestamp.HasValue || GetRequestedFork(ctx) is not { } requestedFork)
            return null;

        IReleaseSpec payloadSpec = specProvider.GetSpec(ForkActivation.TimestampOnly(timestamp.Value));
        string? payloadForkSegment = SszRestPaths.GetEngineApiForkName(payloadSpec);
        return string.Equals(payloadForkSegment, requestedFork, StringComparison.OrdinalIgnoreCase)
            ? null
            : $"Eth-Execution-Version fork '{requestedFork}' does not match the fork for timestamp {timestamp.Value}";
    }
}
