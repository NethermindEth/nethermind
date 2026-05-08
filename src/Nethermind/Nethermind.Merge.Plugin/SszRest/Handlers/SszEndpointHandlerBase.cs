// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Nethermind.Core;
using Nethermind.JsonRpc;

namespace Nethermind.Merge.Plugin.SszRest.Handlers;

/// <summary>
/// Base class for SSZ-REST endpoint handlers. Encoders write directly into the
/// response <see cref="PipeWriter"/>; no intermediate pooled buffer is held.
/// </summary>
public abstract class SszEndpointHandlerBase : ISszEndpointHandler
{
    private const string OctetStream = "application/octet-stream";

    public abstract string HttpMethod { get; }

    public abstract string Resource { get; }

    public virtual int? Version => null;

    public virtual bool AcceptsPathExtra => false;

    /// <inheritdoc/>
    public abstract Task HandleAsync(HttpContext ctx, int version, ReadOnlyMemory<char> extra, ReadOnlyMemory<byte> body);

    private static async Task WriteSszAsync<T>(HttpContext ctx, T value, Func<T, IBufferWriter<byte>, int> encode)
    {
        // GetSpan/Advance buffer into the response pipe without starting the response;
        // headers (incl. ContentLength) remain settable until FlushAsync.
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
            // Encode advanced bytes into the pipe before throwing; we can't rewind.
            // Abort the connection so the CL doesn't see a 500 with garbled-binary body.
            ctx.Abort();
            throw;
        }
        Debug.Assert(pipe.UnflushedBytes - before == length, "encoder advanced wrong byte count");
        ctx.Response.ContentType = OctetStream;
        ctx.Response.ContentLength = length;
        ctx.Response.StatusCode = StatusCodes.Status200OK;
        await pipe.FlushAsync(ctx.RequestAborted);
        await ctx.Response.CompleteAsync();
    }

    protected static Task WriteSszResultAsync<T>(HttpContext ctx, ResultWrapper<T> result, Func<T, IBufferWriter<byte>, int> encode) =>
        result switch
        {
            { Result.ResultType: not ResultType.Success } => WriteErrorAsync(ctx, ErrorCodeToHttpStatus(result.ErrorCode), result.Result.Error ?? "Unknown error"),
            { Data: null } => SetNoContent(ctx),
            { Data: var data } => WriteSszAsync(ctx, data, encode)
        };

    private static Task SetNoContent(HttpContext ctx)
    {
        ctx.Response.StatusCode = StatusCodes.Status204NoContent;
        return Task.CompletedTask;
    }

    public static async Task WriteErrorAsync(HttpContext ctx, int status, string message)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "text/plain";
        await ctx.Response.WriteAsync(message, ctx.RequestAborted);
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
