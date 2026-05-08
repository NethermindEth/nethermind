// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.JsonRpc;

namespace Nethermind.Merge.Plugin.SszRest.Handlers;

/// <summary>
/// Base class for SSZ-REST endpoint handlers.
/// Provides shared HTTP response-writing helpers so no handler
/// duplicates the ArrayPool-return, status-code, or Content-Type logic.
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

    private static async Task WriteSszPooledAsync(HttpContext ctx, ArrayPoolSpan<byte> span)
    {
        try
        {
            ctx.Response.ContentType = OctetStream;
            ctx.Response.ContentLength = span.Length;
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            await ctx.Response.Body.WriteAsync(span.AsMemory(), ctx.RequestAborted);
            await ctx.Response.CompleteAsync();
        }
        finally
        {
            span.Dispose();
        }
    }

    protected static Task WriteSszResultAsync<T>(HttpContext ctx, ResultWrapper<T> result, Func<T, ArrayPoolSpan<byte>> encode) =>
        result switch
        {
            { Result.ResultType: not ResultType.Success } => WriteErrorAsync(ctx, ErrorCodeToHttpStatus(result.ErrorCode), result.Result.Error ?? "Unknown error"),
            { Data: null } => SetNoContent(ctx),
            { Data: var data } => WriteSszPooledAsync(ctx, encode(data))
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
