// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;

namespace Nethermind.Merge.Plugin.SszRest.Handlers;

/// <summary>
/// Handles <c>POST /engine/v1/blobs</c>, the SSZ-REST equivalent of
/// <c>engine_getBlobsV1</c>
/// </summary>
public sealed class GetBlobsV1SszHandler(
    IAsyncHandler<byte[][], IEnumerable<BlobAndProofV1?>> handler) : SszEndpointHandlerBase
{
    public override string HttpMethod => "POST";
    public override string Resource => "blobs";
    public override int? Version => 1;

    public override async Task HandleAsync(HttpContext ctx, int version, string extra, byte[] body)
    {
        byte[][] hashes = SszCodec.DecodeGetBlobsRequest(body);
        await WriteSszResultAsync(ctx, await handler.HandleAsync(hashes), SszCodec.EncodeGetBlobsV1Response);
    }
}

/// <summary>
/// Handles <c>POST /engine/v{N}/blobs</c> for v2 and v3, the SSZ-REST equivalent of
/// <c>engine_getBlobsV2</c> / <c>engine_getBlobsV3</c>.
/// </summary>
public sealed class GetBlobsV2SszHandler(
    int version,
    bool allowPartialReturn,
    IAsyncHandler<GetBlobsHandlerV2Request, IEnumerable<BlobAndProofV2?>?> handler,
    Func<IEnumerable<BlobAndProofV2?>, (byte[] buffer, int length)> encode) : SszEndpointHandlerBase
{
    public override string HttpMethod => "POST";
    public override string Resource => "blobs";
    public override int? Version => version;

    public override async Task HandleAsync(HttpContext ctx, int v, string extra, byte[] body)
    {
        byte[][] hashes = SszCodec.DecodeGetBlobsRequest(body);
        await WriteSszResultAsync(ctx,
            await handler.HandleAsync(new GetBlobsHandlerV2Request(hashes, AllowPartialReturn: allowPartialReturn)),
            blobs => encode(blobs!));
    }
}
