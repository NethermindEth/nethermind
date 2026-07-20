// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Handlers;

namespace Nethermind.Merge.Plugin.SszRest.Handlers;

/// <summary>
/// Handles <c>GET /engine/v2/inclusion_list/{block_hash}</c> for Bogota (EIP-7805 / FOCIL), the
/// SSZ-REST equivalent of <c>engine_getInclusionListV1</c>. The block hash travels as a path segment.
/// </summary>
public sealed class GetInclusionListSszHandler(IEngineRpcModule engineModule) : SszEndpointHandlerBase
{
    // Spec: block_hash is a hex-encoded Bytes32 (64 hex chars).
    private const int BlockHashHexLength = Hash256.Size * 2;

    public override string HttpMethod => "GET";
    public override string Resource => SszRestPaths.InclusionList;
    public override int? Version => EngineApiVersions.GetInclusionList.V1;
    public override bool AcceptsPathExtra => true;

    public override async Task HandleAsync(HttpContext ctx, int version, ReadOnlyMemory<char> extra, ReadOnlySequence<byte> body)
    {
        ctx.Response.Headers.CacheControl = "no-store";
        if (!TryParseBlockHash(extra.Span, out Hash256? blockHash, out string err))
        {
            await WriteErrorAsync(ctx, StatusCodes.Status400BadRequest, err);
            return;
        }

        // WriteSszResultAsync disposes the ResultWrapper (and its IDisposable InclusionListBytes) after encoding.
        ResultWrapper<InclusionListBytes> result = await engineModule.engine_getInclusionListV1(blockHash);
        await WriteSszResultAsync(ctx, result, SszCodec.EncodeInclusionListResponse);
    }

    private static bool TryParseBlockHash(ReadOnlySpan<char> extra, [NotNullWhen(true)] out Hash256? blockHash, out string err)
    {
        blockHash = null;

        if (extra.IsEmpty)
        {
            err = "Missing block hash";
            return false;
        }

        ReadOnlySpan<char> hex = extra.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? extra[2..] : extra;
        if (hex.Length != BlockHashHexLength)
        {
            err = $"Invalid block hash: '{extra}' (expected {BlockHashHexLength} hex chars)";
            return false;
        }

        Span<byte> bytes = stackalloc byte[Hash256.Size];
        if (Convert.FromHexString(hex, bytes, out _, out _) != OperationStatus.Done)
        {
            err = $"Invalid block hash: '{extra}'";
            return false;
        }

        blockHash = new Hash256(bytes);
        err = string.Empty;
        return true;
    }
}
