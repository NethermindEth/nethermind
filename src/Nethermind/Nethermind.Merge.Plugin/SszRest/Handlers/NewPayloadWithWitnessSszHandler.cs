// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Nethermind.Consensus.Stateless;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Serialization.Json;

namespace Nethermind.Merge.Plugin.SszRest.Handlers;

/// <summary>
/// Handles <c>POST /new-payload-with-witness</c>. Per execution-apis#773 the request is JSON
/// (same shape as <c>engine_newPayloadV5</c> params) and the response is SSZ-encoded
/// <c>NewPayloadWithWitnessResponseV1</c> — the only mixed-format endpoint in the SSZ-REST surface.
/// </summary>
public sealed class NewPayloadWithWitnessSszHandler(
    IEngineRpcModule engineModule) : SszEndpointHandlerBase
{

    public override string HttpMethod => "POST";

    // Non-versioned path; SszMiddleware routes via a dedicated fast path for this resource.
    public override string Resource => SszRestPaths.NewPayloadWithWitness;
    public override int? Version => null;

    public override async Task HandleAsync(HttpContext ctx, int version, ReadOnlyMemory<char> extra, ReadOnlySequence<byte> body)
    {
        NewPayloadV5Params? request = DeserializeRequest(body);
        if (request is null)
        {
            await WriteErrorAsync(ctx, StatusCodes.Status400BadRequest, "Malformed JSON body or invalid parameter shapes", ErrorCodes.ParseError);
            return;
        }

        ResultWrapper<NewPayloadWithWitnessV1Result> result = await engineModule.engine_newPayloadWithWitness(
            request.ExecutionPayload,
            request.ExpectedBlobVersionedHashes,
            request.ParentBeaconBlockRoot,
            request.ExecutionRequests);

        using (result)
        {
            if (result.Result.ResultType != ResultType.Success)
            {
                int httpStatus = result.ErrorCode switch
                {
                    MergeErrorCodes.UnsupportedFork => StatusCodes.Status400BadRequest,
                    _ => StatusCodes.Status500InternalServerError,
                };
                int jsonRpcCode = result.ErrorCode switch
                {
                    MergeErrorCodes.UnsupportedFork => MergeErrorCodes.UnsupportedFork,
                    _ => ErrorCodes.InternalError,
                };
                await WriteErrorAsync(ctx, httpStatus, result.Result.Error ?? "Unknown error", jsonRpcCode);
                return;
            }

            NewPayloadWithWitnessV1Result witnessResult = result.Data!;
            PayloadStatusV1 payloadStatus = new()
            {
                Status = witnessResult.Status,
                LatestValidHash = witnessResult.LatestValidHash,
                ValidationError = witnessResult.ValidationError
            };
            await WriteSszNewPayloadWithWitnessAsync(ctx, payloadStatus, witnessResult.ExecutionWitness);
        }
    }

    // Witness ownership stays with the caller — the enclosing ResultWrapper disposes it.
    private static async Task WriteSszNewPayloadWithWitnessAsync(HttpContext ctx, PayloadStatusV1 status, Witness? witness)
    {
        ArrayBufferWriter<byte> buffer = new();
        int length;
        try
        {
            length = SszCodec.EncodeNewPayloadWithWitnessResponse(status, witness, buffer);
        }
        catch
        {
            ctx.Abort();
            throw;
        }

        ctx.Response.ContentType = "application/octet-stream";
        ctx.Response.ContentLength = length;
        ctx.Response.StatusCode = StatusCodes.Status200OK;

        System.IO.Pipelines.PipeWriter pipe = ctx.Response.BodyWriter;
        try
        {
            await pipe.WriteAsync(buffer.WrittenMemory, ctx.RequestAborted);
        }
        catch
        {
            ctx.Abort();
            throw;
        }

        await ctx.Response.CompleteAsync();
    }

    private static NewPayloadV5Params? DeserializeRequest(ReadOnlySequence<byte> body)
    {
        try
        {
            Utf8JsonReader reader = new(body);

            if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray) return null;

            if (!reader.Read()) return null;
            ExecutionPayloadV4? payload = JsonSerializer.Deserialize<ExecutionPayloadV4>(
                ref reader, EthereumJsonSerializer.JsonOptions);
            if (payload is null) return null;

            if (!reader.Read()) return null;
            Hash256?[]? blobHashes = JsonSerializer.Deserialize<Hash256?[]>(
                ref reader, EthereumJsonSerializer.JsonOptions);

            if (!reader.Read()) return null;
            Hash256? parentBeaconBlockRoot = JsonSerializer.Deserialize<Hash256?>(
                ref reader, EthereumJsonSerializer.JsonOptions);

            if (!reader.Read()) return null;
            byte[][]? executionRequests = JsonSerializer.Deserialize<byte[][]>(
                ref reader, EthereumJsonSerializer.JsonOptions);

            if (!reader.Read() || reader.TokenType != JsonTokenType.EndArray) return null;

            return new NewPayloadV5Params(payload, blobHashes ?? [], parentBeaconBlockRoot, executionRequests);
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException or OverflowException or ArgumentException)
        {
            return null;
        }
    }

    private sealed record NewPayloadV5Params(
        ExecutionPayloadV4 ExecutionPayload,
        Hash256?[] ExpectedBlobVersionedHashes,
        Hash256? ParentBeaconBlockRoot,
        byte[][]? ExecutionRequests);
}
