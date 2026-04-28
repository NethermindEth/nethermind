// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Nethermind.Core;
using Nethermind.JsonRpc;

namespace Nethermind.Merge.Plugin.SszRest.Handlers;

/// <summary>
/// Base class for SSZ-REST endpoint handlers.
/// Provides shared HTTP response-writing helpers so no handler
/// duplicates the ArrayPool-return, status-code, or Content-Type logic.
/// </summary>
public abstract class SszEndpointHandlerBase : ISszEndpointHandler
{
    protected const string OctetStream = "application/octet-stream";

    public abstract string HttpMethod { get; }

    public abstract string Resource { get; }

    public virtual int? Version => null;

    /// <inheritdoc/>
    public abstract Task HandleAsync(HttpContext ctx, int version, string extra, byte[] body);

    protected static bool TryParsePayloadId(string extra, out byte[] id, out string err)
    {
        if (extra.Length == 0)
        {
            id = [];
            err = "Missing payload ID";
            return false;
        }
        string hex = extra.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? extra[2..] : extra;
        id = Convert.FromHexString(hex.AsSpan());
        err = string.Empty;
        return true;
    }

    protected static Task WriteSszPooledAsync(HttpContext ctx, (byte[] buffer, int length) pooled)
        => WriteSszPooledAsync(ctx, pooled.buffer, pooled.length);

    protected static async Task WriteSszPooledAsync(HttpContext ctx, byte[] buffer, int length)
    {
        try
        {
            ctx.Response.ContentType = OctetStream;
            ctx.Response.ContentLength = length;
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            await ctx.Response.Body.WriteAsync(buffer.AsMemory(0, length), ctx.RequestAborted);
            await ctx.Response.CompleteAsync();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    protected static async Task WriteSszResultAsync<T>(
        HttpContext ctx,
        ResultWrapper<T> result,
        Func<T, (byte[] buffer, int length)> encode)
    {
        if (result.Result != Result.Success)
        {
            await WriteErrorAsync(ctx, ErrorCodeToHttpStatus(result.ErrorCode),
                result.Result.Error ?? "Unknown error");
            return;
        }
        if (result.Data is null)
        {
            ctx.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }
        await WriteSszPooledAsync(ctx, encode(result.Data));
    }

    public static async Task WriteErrorAsync(HttpContext ctx, int status, string message)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "text/plain";
        await ctx.Response.WriteAsync(message);
    }

    protected static int ErrorCodeToHttpStatus(int errorCode) => errorCode switch
    {
        MergeErrorCodes.UnknownPayload => StatusCodes.Status404NotFound,
        MergeErrorCodes.InvalidForkchoiceState => StatusCodes.Status409Conflict,
        MergeErrorCodes.InvalidPayloadAttributes => StatusCodes.Status422UnprocessableEntity,
        MergeErrorCodes.TooLargeRequest => StatusCodes.Status413PayloadTooLarge,
        ErrorCodes.InternalError => StatusCodes.Status500InternalServerError,
        _ => StatusCodes.Status400BadRequest
    };
}
