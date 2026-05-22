// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Nethermind.Core;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Merge.Plugin.SszRest.Handlers;

/// <summary>JSON error envelope written by <see cref="SszEndpointHandlerBase.WriteErrorAsync"/>.</summary>
public sealed record SszErrorResponse(int Code, string Message);

/// <summary>Base for SSZ-REST endpoint handlers. Encoders write directly into the response <see cref="PipeWriter"/>.</summary>
public abstract class SszEndpointHandlerBase : ISszEndpointHandler
{
    private const string OctetStream = "application/octet-stream";

    public abstract string HttpMethod { get; }

    public abstract string Resource { get; }

    public virtual int? Version => null;

    public virtual bool AcceptsPathExtra => false;

    /// <inheritdoc/>
    public abstract Task HandleAsync(HttpContext ctx, int version, ReadOnlyMemory<char> extra, ReadOnlySequence<byte> body);

    private static async Task WriteSszAsync<T>(HttpContext ctx, T value, Func<T, IBufferWriter<byte>, int> encode)
    {
        PipeWriter pipe = ctx.Response.BodyWriter;
        Debug.Assert(!ctx.Response.HasStarted, "response must not have started before SSZ encode");
        long before = pipe.UnflushedBytes;
        int length;
        try
        {
            length = encode(value, pipe);
        }
        catch
        {
            // Encoder already wrote bytes into the pipe — abort the connection so the CL
            // doesn't see a 500 with a half-written binary body.
            ctx.Abort();
            throw;
        }
        Debug.Assert(pipe.UnflushedBytes - before == length, "encoder advanced wrong byte count");

        if (length == 0)
        {
            // Empty encode output is treated as 204, matching the Data=null branch in WriteSszResultAsync.
            ctx.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        ctx.Response.ContentType = OctetStream;
        ctx.Response.ContentLength = length;
        ctx.Response.StatusCode = StatusCodes.Status200OK;
        await pipe.FlushAsync(ctx.RequestAborted);
        await ctx.Response.CompleteAsync();
    }

    protected static async Task WriteSszResultAsync<T>(HttpContext ctx, ResultWrapper<T> result, Func<T, IBufferWriter<byte>, int> encode)
    {
        using (result)
        {
            await (result switch
            {
                { Result.ResultType: not ResultType.Success } => WriteErrorAsync(ctx, ErrorCodeToHttpStatus(result.ErrorCode), result.Result.Error ?? "Unknown error", result.ErrorCode),
                { Data: null } => SetNoContent(ctx),
                { Data: var data } => WriteSszAsync(ctx, data, encode)
            });
        }
    }

    private static Task SetNoContent(HttpContext ctx)
    {
        ctx.Response.StatusCode = StatusCodes.Status204NoContent;
        return Task.CompletedTask;
    }

    public static async Task WriteErrorAsync(HttpContext ctx, int status, string message, int jsonRpcCode = ErrorCodes.InternalError)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        string json = JsonSerializer.Serialize(
            new SszErrorResponse(jsonRpcCode, message),
            EngineApiJsonContext.Default.SszErrorResponse);
        await ctx.Response.WriteAsync(json, ctx.RequestAborted);
    }

    private static int ErrorCodeToHttpStatus(int errorCode) => errorCode switch
    {
        MergeErrorCodes.UnknownPayload => StatusCodes.Status404NotFound,
        MergeErrorCodes.InvalidForkchoiceState => StatusCodes.Status409Conflict,
        MergeErrorCodes.InvalidPayloadAttributes => StatusCodes.Status422UnprocessableEntity,
        MergeErrorCodes.TooLargeRequest => StatusCodes.Status413PayloadTooLarge,
        ErrorCodes.MethodNotFound => StatusCodes.Status404NotFound,
        ErrorCodes.InternalError => StatusCodes.Status500InternalServerError,
        _ => StatusCodes.Status400BadRequest
    };
}
