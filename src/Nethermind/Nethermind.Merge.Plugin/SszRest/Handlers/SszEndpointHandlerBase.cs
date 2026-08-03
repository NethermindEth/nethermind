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
    public abstract Task HandleAsync(HttpContext ctx, int version, ReadOnlyMemory<char> extra, ReadOnlySequence<byte> body);

    /// <summary>
    /// The fork requested via the <c>Eth-Execution-Version</c> header for this fork-scoped request,
    /// as stashed by <see cref="SszMiddleware"/>, or <c>null</c> for unscoped/blob endpoints.
    /// </summary>
    protected static string? GetRequestedFork(HttpContext ctx) =>
        ctx.Items.TryGetValue(SszMiddleware.RouteForkItemKey, out object? fork) ? fork as string : null;

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

        if (length == 0)
        {
            // Encoder produced an empty body for non-null input — semantically equivalent
            // to no-content. Mirrors the SetNoContent path so success-with-empty-body and
            // null-data return the same 204 status.
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

    public static async Task WriteErrorAsync(HttpContext ctx, int status, string message, int? errorCode = null)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/problem+json";

        // Derive the RFC 7807 type URI.  Error-code takes precedence over HTTP status so
        // that two distinct engine errors sharing the same status emit different type URIs.
        string type = errorCode switch
        {
            // JSON-RPC standard codes (-32xxx)
            ErrorCodes.ParseError => "/engine-api/errors/parse-error",

            // Engine-API extension codes (-38xxx)
            MergeErrorCodes.UnknownPayload => "/engine-api/errors/unknown-payload",
            MergeErrorCodes.InvalidForkchoiceState => "/engine-api/errors/invalid-forkchoice",
            MergeErrorCodes.InvalidPayloadAttributes => "/engine-api/errors/invalid-attributes",
            MergeErrorCodes.TooLargeRequest => "/engine-api/errors/request-too-large",
            MergeErrorCodes.UnsupportedFork => "/engine-api/errors/unsupported-fork",
            MergeErrorCodes.ReorgTooDeep => "/engine-api/errors/reorg-too-deep",

            // SSZ-REST-specific internal codes (-39xxx)
            SszRestErrorCodes.SszDecodeError => "/engine-api/errors/ssz-decode-error",
            SszRestErrorCodes.InvalidRequest => "/engine-api/errors/invalid-request",
            SszRestErrorCodes.MethodNotFound => "/engine-api/errors/method-not-found",
            SszRestErrorCodes.UnsupportedMediaType => "/engine-api/errors/unsupported-media-type",
            SszRestErrorCodes.InvalidBody => "/engine-api/errors/invalid-body",

            _ => status switch
            {
                // Fallback mapping for callers that pass no error code.
                // 400: map to invalid-request as the generic fallback (covers malformed
                //      query parameters, structural field errors, etc.)
                StatusCodes.Status400BadRequest => "/engine-api/errors/invalid-request",
                StatusCodes.Status401Unauthorized => "/engine-api/errors/unauthorized",
                // 404: default to method-not-found; unknown-payload is covered above via
                //      MergeErrorCodes.UnknownPayload.
                StatusCodes.Status404NotFound => "/engine-api/errors/method-not-found",
                StatusCodes.Status409Conflict => "/engine-api/errors/invalid-forkchoice",
                StatusCodes.Status413PayloadTooLarge => "/engine-api/errors/request-too-large",
                StatusCodes.Status415UnsupportedMediaType => "/engine-api/errors/unsupported-media-type",
                StatusCodes.Status422UnprocessableEntity => "/engine-api/errors/invalid-attributes",
                StatusCodes.Status500InternalServerError => "/engine-api/errors/internal",
                StatusCodes.Status503ServiceUnavailable => "/engine-api/errors/service-unavailable",
                _ => "/engine-api/errors/error"
            }
        };

        bool omitDetail = type is "/engine-api/errors/ssz-decode-error"
                               or "/engine-api/errors/unauthorized"
                       || string.IsNullOrEmpty(message);

        string body = omitDetail
            ? $"{{\"type\":{JsonSerializer.Serialize(type)}}}"
            : $"{{\"type\":{JsonSerializer.Serialize(type)},\"detail\":{JsonSerializer.Serialize(message)}}}";

        await ctx.Response.WriteAsync(body, ctx.RequestAborted);
    }

    protected static int ErrorCodeToHttpStatus(int errorCode) => errorCode switch
    {
        MergeErrorCodes.UnknownPayload => StatusCodes.Status404NotFound,
        MergeErrorCodes.InvalidForkchoiceState => StatusCodes.Status409Conflict,
        MergeErrorCodes.ReorgTooDeep => StatusCodes.Status409Conflict,
        MergeErrorCodes.InvalidPayloadAttributes => StatusCodes.Status422UnprocessableEntity,
        MergeErrorCodes.TooLargeRequest => StatusCodes.Status413PayloadTooLarge,
        MergeErrorCodes.UnsupportedFork => StatusCodes.Status400BadRequest,
        ErrorCodes.MethodNotFound => StatusCodes.Status404NotFound,
        ErrorCodes.InternalError => StatusCodes.Status500InternalServerError,
        _ => StatusCodes.Status400BadRequest
    };
}
