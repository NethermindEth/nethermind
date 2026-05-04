// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;
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

    public virtual bool AcceptsPathExtra => false;

    /// <inheritdoc/>
    public abstract Task HandleAsync(HttpContext ctx, int version, string extra, ReadOnlyMemory<byte> body);

    protected static bool TryParsePayloadId(string extra, out byte[] id, out string err)
    {
        if (extra.Length == 0)
        {
            id = [];
            err = "Missing payload ID";
            return false;
        }
        string hex = extra.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? extra[2..] : extra;
        if (hex.Length == 0 || hex.Length % 2 != 0)
        {
            id = [];
            err = $"Invalid payload ID: '{extra}'";
            return false;
        }
        try
        {
            id = Bytes.FromHexString(hex);
        }
        catch (FormatException)
        {
            id = [];
            err = $"Invalid payload ID: '{extra}'";
            return false;
        }
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

    /// <summary>
    /// Overload for encoders that return <see cref="ArrayPoolSpan{T}"/> instead of a
    /// <c>(byte[] buffer, int length)</c> tuple. The span is written then disposed.
    /// Uses <see cref="ArrayPoolSpan{T}.AsMemory()"/> to write directly from the rented
    /// buffer without an intermediate copy or extra rent.
    /// </summary>
    protected static async Task WriteSszPooledAsync(HttpContext ctx, ArrayPoolSpan<byte> span)
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

    /// <summary>
    /// Overload for encoders that return <see cref="ArrayPoolSpan{T}"/> instead of a
    /// <c>(byte[] buffer, int length)</c> tuple.
    /// </summary>
    protected static async Task WriteSszResultAsync<T>(
        HttpContext ctx,
        ResultWrapper<T> result,
        Func<T, ArrayPoolSpan<byte>> encode)
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

    internal static IReadOnlyList<T?> AsReadOnlyList<T>(IEnumerable<T?> source)
    {
        if (source is IReadOnlyList<T?> list) return list;
        if (source is ICollection<T?> collection)
        {
            T?[] array = new T?[collection.Count];
            collection.CopyTo(array, 0);
            return array;
        }
        return [.. source];
    }

    protected static int ErrorCodeToHttpStatus(int errorCode) => errorCode switch
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
