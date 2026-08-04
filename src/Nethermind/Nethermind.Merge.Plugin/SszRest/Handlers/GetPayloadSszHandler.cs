// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace Nethermind.Merge.Plugin.SszRest.Handlers;

/// <summary>
/// Handles <c>GET /engine/v2/payloads/{payload_id}</c>, the SSZ-REST equivalent of
/// <c>engine_getPayloadV{N}</c> (the version is selected by the <c>Eth-Execution-Version</c> header).
/// </summary>
public sealed class GetPayloadSszHandler<TVersion, TResult>(IEngineRpcModule engine)
    : SszEndpointHandlerBase
    where TVersion : struct, IGetPayloadVersion<TResult>
    where TResult : class
{
    // Spec: payload_id is hex-encoded Bytes8 (16 hex chars).
    private const int PayloadIdHexLength = 16;
    private const int PayloadIdByteLength = 8;

    public override string HttpMethod => "GET";
    public override string Resource => SszRestPaths.Payloads;
    public override int? Version => TVersion.VersionNumber;
    public override bool AcceptsPathExtra => true;

    public override async Task HandleAsync(HttpContext ctx, int v, ReadOnlyMemory<char> extra, ReadOnlySequence<byte> body)
    {
        ctx.Response.Headers.CacheControl = "no-store";
        if (TryParsePayloadId(extra.Span, out byte[] id, out string err))
        {
            await WriteSszResultAsync(ctx, await TVersion.Call(engine, id), static (d, w) => TVersion.Encode(d!, w));
        }
        else
        {
            await WriteErrorAsync(ctx, StatusCodes.Status400BadRequest, err);
        }
    }

    private static bool TryParsePayloadId(ReadOnlySpan<char> extra, out byte[] id, out string err)
    {
        if (extra.Length == 0)
            return Out([], "Missing payload ID", out id, out err);

        ReadOnlySpan<char> hex = extra.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? extra[2..] : extra;
        if (hex.Length != PayloadIdHexLength)
            return Out([], $"Invalid payload ID: '{extra}' (expected {PayloadIdHexLength} hex chars)", out id, out err);

        // stackalloc so a malformed hex never allocates; on success we materialize
        // one byte[8] for the IEngineRpcModule call (JSON-RPC binds byte[]).
        Span<byte> stack = stackalloc byte[PayloadIdByteLength];
        if (Convert.FromHexString(hex, stack, out _, out _) != OperationStatus.Done)
            return Out([], $"Invalid payload ID: '{extra}'", out id, out err);

        return Out(stack.ToArray(), error: null, out id, out err);
    }

    /// <summary>
    /// Single result-projection helper. Pass <c>null</c> for <paramref name="error"/>
    /// on success; non-null is treated as failure.
    /// </summary>
    private static bool Out(byte[] value, string? error, out byte[] id, out string err)
    {
        id = value;
        err = error ?? string.Empty;
        return error is null;
    }
}
