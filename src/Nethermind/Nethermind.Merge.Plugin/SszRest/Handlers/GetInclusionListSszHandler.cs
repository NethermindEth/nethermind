// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Handlers;

namespace Nethermind.Merge.Plugin.SszRest.Handlers;

/// <summary>SSZ-REST equivalent of <c>engine_getInclusionListV1</c>.</summary>
public sealed class GetInclusionListSszHandler(IEngineRpcModule engineModule) : SszEndpointHandlerBase
{
    private const int HashHexLength = 2 * Hash256.Size;

    public override string HttpMethod => "GET";
    public override string Resource => SszRestPaths.InclusionList;
    public override int? Version => EngineApiVersions.GetInclusionList.V1;
    public override bool AcceptsPathExtra => true;

    public override async Task HandleAsync(HttpContext ctx, int version, ReadOnlyMemory<char> extra, ReadOnlySequence<byte> body)
    {
        ctx.Response.Headers.CacheControl = "no-store";

        if (!TryParseParentBlockHash(extra.Span, out Hash256? parentBlockHash))
        {
            await WriteErrorAsync(ctx, StatusCodes.Status400BadRequest, $"Invalid parent block hash: '{extra}'");
            return;
        }

        // WriteSszResultAsync disposes the ResultWrapper (and its IDisposable InclusionListBytes) after encoding.
        ResultWrapper<InclusionListBytes> result = await engineModule.engine_getInclusionListV1(parentBlockHash);
        await WriteSszResultAsync(ctx, result, SszCodec.EncodeInclusionListResponse);
    }

    private static bool TryParseParentBlockHash(ReadOnlySpan<char> extra, out Hash256? parentBlockHash)
    {
        parentBlockHash = null;
        if (extra.Length == 0) return true;

        ReadOnlySpan<char> hex = extra.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? extra[2..] : extra;
        if (hex.Length != HashHexLength) return false;

        Span<byte> bytes = stackalloc byte[Hash256.Size];
        if (Convert.FromHexString(hex, bytes, out _, out int written) != OperationStatus.Done || written != Hash256.Size)
            return false;

        parentBlockHash = new Hash256(bytes);
        return true;
    }
}
