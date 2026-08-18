// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Merge.Plugin.SszRest.Handlers;

/// <summary>
/// Handles <c>POST /engine/v2/forkchoice</c> for Bogota (EIP-7805 / FOCIL), the SSZ-REST equivalent of
/// <c>engine_forkchoiceUpdatedV5</c>. Dedicated rather than a <see cref="ForkchoiceUpdatedSszHandler{TVersion,TWire}"/>
/// descriptor because V5 is the only forkchoiceUpdated version returning <see cref="ForkchoiceUpdatedV2Result"/>
/// (with the inclusion-list-satisfied flag) instead of <see cref="ForkchoiceUpdatedV1Result"/>.
/// </summary>
public sealed class ForkchoiceUpdatedV5SszHandler(IEngineRpcModule engineModule, ISpecProvider specProvider) : SszEndpointHandlerBase
{
    public override string HttpMethod => "POST";
    public override string Resource => SszRestPaths.Forkchoice;
    public override int? Version => EngineApiVersions.Fcu.V5;

    public override async Task HandleAsync(HttpContext ctx, int version, ReadOnlyMemory<char> extra, ReadOnlySequence<byte> body)
    {
        ForkchoiceUpdatedV5RequestWire.Decode(body, out ForkchoiceUpdatedV5RequestWire wire);

        if (GetForkMismatchMessage(ctx, ForkchoiceUpdatedHelpers.FirstTimestamp(wire.PayloadAttributes)) is { } mismatch)
        {
            await WriteErrorAsync(ctx, StatusCodes.Status400BadRequest, mismatch, MergeErrorCodes.UnsupportedFork);
            return;
        }

        ForkchoiceStateV1 state = SszCodec.ForkchoiceStateV1FromWire(wire.ForkchoiceState);
        PayloadAttributes? attrs = wire.PayloadAttributes is { Length: > 0 } a ? SszCodec.PayloadAttributesFromWire(a[0]) : null;
        byte[]? custody = ForkchoiceUpdatedHelpers.CustodyColumnsToBytes(wire.CustodyColumns);

        ResultWrapper<ForkchoiceUpdatedV2Result> result = await engineModule.engine_forkchoiceUpdatedV5(state, attrs, custody);
        await WriteSszResultAsync(ctx, result, SszCodec.EncodeForkchoiceUpdatedResponseV2);
    }

    // Mirrors ForkchoiceUpdatedSszHandler's fork check (that handler is generic over V1-returning descriptors).
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
