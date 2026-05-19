// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Nethermind.Blockchain;
using Nethermind.Consensus.Stateless;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Serialization.Json;

namespace Nethermind.Merge.Plugin.SszRest.Handlers;

/// <summary>
/// Handles <c>POST /new-payload-with-witness</c> as specified in the Engine API REST extensions.
/// Accepts the same JSON parameters as <c>engine_newPayloadV5</c> and returns an SSZ-encoded
/// <c>NewPayloadWithWitnessResponseV1</c> that includes the execution witness when status is VALID.
/// </summary>
public sealed class NewPayloadWithWitnessSszHandler(
    IEngineRpcModule engineModule,
    IBlockTree blockTree,
    IWitnessGeneratingBlockProcessingEnvFactory witnessEnvFactory,
    ILogManager logManager) : SszEndpointHandlerBase
{
    private readonly ILogger _logger = logManager.GetClassLogger<NewPayloadWithWitnessSszHandler>();

    public override string HttpMethod => "POST";

    // This handler uses a non-versioned path outside /engine/v{N}/.
    // The SszMiddleware dispatches to it via a dedicated fast path for this resource constant.
    public override string Resource => SszRestPaths.NewPayloadWithWitness;

    // Version is null, this endpoint has no version prefix in its path.
    public override int? Version => null;

    public override async Task HandleAsync(HttpContext ctx, int version, ReadOnlyMemory<char> extra, ReadOnlySequence<byte> body)
    {
        string? contentType = ctx.Request.ContentType;
        if (contentType is null || !contentType.Contains("application/json", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Response.Headers["Accept"] = "application/json";
            await WriteErrorAsync(ctx, StatusCodes.Status415UnsupportedMediaType,
                "Content-Type must be application/json", ErrorCodes.ParseError);
            return;
        }

        NewPayloadV5Params? request = DeserializeRequest(body);
        if (request is null)
        {
            await WriteErrorAsync(ctx, StatusCodes.Status400BadRequest, "Malformed JSON body or invalid parameter shapes", ErrorCodes.ParseError);
            return;
        }

        ResultWrapper<PayloadStatusV1> result = await engineModule.engine_newPayloadV5(
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
                    _ => StatusCodes.Status500InternalServerError
                };
                int jsonRpcCode = result.ErrorCode switch
                {
                    MergeErrorCodes.UnsupportedFork => MergeErrorCodes.UnsupportedFork,
                    _ => ErrorCodes.InternalError
                };
                await WriteErrorAsync(ctx, httpStatus, result.Result.Error ?? "Unknown error", jsonRpcCode);
                return;
            }

            PayloadStatusV1 status = result.Data!;
            Witness? witness = null;

            if (status.Status == PayloadStatus.Valid)
            {
                // TODO(perf): TryGenerateWitness re-executes the block via a second
                // WitnessCollector.GetWitnessForExistingBlock → ProcessOne call after
                // engine_newPayloadV5 has already processed it once. The parent spec
                // (execution-apis #773) was designed to eliminate this double-execution.
                // Wiring witness collection into the primary processing path is a follow-up.
                // https://github.com/NethermindEth/nethermind/issues/11636.
                witness = TryGenerateWitness(request.ExecutionPayload);

                if (witness is null)
                {
                    if (_logger.IsError)
                        _logger.Error(
                            $"Payload executed with VALID status but the execution witness could " +
                            $"not be generated for block {request.ExecutionPayload.BlockHash}. " +
                            $"The block has been accepted; returning witness=None per spec Union[None, T] arm.");
                }
            }

            await WriteSszNewPayloadWithWitnessAsync(ctx, status, witness);
        }
    }

    private static async Task WriteSszNewPayloadWithWitnessAsync(HttpContext ctx, PayloadStatusV1 status, Witness? witness)
    {
        using Witness? w = witness;

        ArrayBufferWriter<byte> buffer = new();
        int length;
        try
        {
            length = SszCodec.EncodeNewPayloadWithWitnessResponse(status, w, buffer);
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

    private Witness? TryGenerateWitness(ExecutionPayloadV4 executionPayload)
    {
        BlockDecodingResult decodingResult = executionPayload.TryGetBlock();
        Block? block = decodingResult.Block;
        if (block is null)
        {
            if (_logger.IsWarn)
                _logger.Warn($"Witness generation skipped: could not decode block from ExecutionPayloadV4 " +
                             $"(hash={executionPayload.BlockHash}). Decode error: {decodingResult.Error}");
            return null;
        }

        BlockHeader? parent = blockTree.FindHeader(block.ParentHash!, BlockTreeLookupOptions.DoNotCreateLevelIfMissing);
        if (parent is null)
        {
            if (_logger.IsWarn)
                _logger.Warn($"Witness generation skipped: parent header not found for block " +
                             $"{block.Hash} (parentHash={block.ParentHash}).");
            return null;
        }

        try
        {
            using IWitnessGeneratingBlockProcessingEnvScope scope = witnessEnvFactory.CreateScope();
            IExistingBlockWitnessCollector collector = scope.Env.CreateExistingBlockWitnessCollector();
            return collector.GetWitnessForExistingBlock(parent, block);
        }
        catch (OperationCanceledException ex)
        {
            if (_logger.IsWarn)
                _logger.Warn($"Witness generation cancelled for block {block.Hash}: {ex.Message}");
            return null;
        }
        catch (Exception ex)
        {
            if (_logger.IsError)
                _logger.Error($"Witness generation failed for block {block.Hash}: {ex.Message}", ex);
            return null;
        }
    }

    private static NewPayloadV5Params? DeserializeRequest(ReadOnlySequence<byte> body)
    {
        try
        {
            ReadOnlySpan<byte> span = body.IsSingleSegment
                ? body.FirstSpan
                : body.ToArray();

            Utf8JsonReader reader = new(span);

            if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray) return null;

            if (!reader.Read()) return null;
            ExecutionPayloadV4? payload = JsonSerializer.Deserialize<ExecutionPayloadV4>(ref reader, EthereumJsonSerializer.JsonOptions);
            if (payload is null) return null;

            if (!reader.Read()) return null;
            byte[]?[]? blobHashes = JsonSerializer.Deserialize<byte[]?[]>(ref reader, EthereumJsonSerializer.JsonOptions);

            if (!reader.Read()) return null;
            Hash256? parentBeaconBlockRoot = JsonSerializer.Deserialize<Hash256?>(ref reader, EthereumJsonSerializer.JsonOptions);

            if (!reader.Read()) return null;
            byte[][]? executionRequests = JsonSerializer.Deserialize<byte[][]>(ref reader, EthereumJsonSerializer.JsonOptions);

            if (!reader.Read() || reader.TokenType != JsonTokenType.EndArray) return null;

            return new NewPayloadV5Params(payload, blobHashes ?? [], parentBeaconBlockRoot, executionRequests);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record NewPayloadV5Params(
        ExecutionPayloadV4 ExecutionPayload,
        byte[]?[] ExpectedBlobVersionedHashes,
        Hash256? ParentBeaconBlockRoot,
        byte[][]? ExecutionRequests);
}
