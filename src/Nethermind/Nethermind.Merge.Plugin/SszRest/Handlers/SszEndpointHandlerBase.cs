// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
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
    protected const string OctetStream = "application/octet-stream";

    public abstract string HttpMethod { get; }

    public abstract string Resource { get; }

    public virtual int? Version => null;

    public virtual bool AcceptsPathExtra => false;

    /// <inheritdoc/>
    public abstract Task HandleAsync(HttpContext ctx, int version, string extra, ReadOnlyMemory<byte> body);

    // Per execution-apis #764 ssz-encoding spec: "{payload_id} MUST be validated as a
    // well-formed hex-encoded Bytes8 before processing." Bytes8 = exactly 16 hex chars.
    private const int PayloadIdHexLength = 16;
    private const int PayloadIdByteLength = 8;

    protected static bool TryParsePayloadId(ReadOnlySpan<char> extra, out byte[] id, out string err)
    {
        if (extra.Length == 0)
        {
            id = [];
            err = "Missing payload ID";
            return false;
        }
        ReadOnlySpan<char> hex = extra.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? extra[2..] : extra;
        if (hex.Length != PayloadIdHexLength)
        {
            id = [];
            err = $"Invalid payload ID: '{extra}' (expected {PayloadIdHexLength} hex chars)";
            return false;
        }
        byte[] dest = new byte[PayloadIdByteLength];
        OperationStatus status = Convert.FromHexString(hex, dest, out _, out _);
        if (status != OperationStatus.Done)
        {
            id = [];
            err = $"Invalid payload ID: '{extra}'";
            return false;
        }
        id = dest;
        err = string.Empty;
        return true;
    }

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
        await ctx.Response.WriteAsync(message, ctx.RequestAborted);
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
