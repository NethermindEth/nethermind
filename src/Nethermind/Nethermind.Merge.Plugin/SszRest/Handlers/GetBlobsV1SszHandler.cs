// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;

namespace Nethermind.Merge.Plugin.SszRest.Handlers;

/// <summary>
/// Handles <c>POST /engine/v1/blobs</c>, the SSZ-REST equivalent of
/// <c>engine_getBlobsV1</c>.
/// </summary>
public sealed class GetBlobsV1SszHandler(
    IAsyncHandler<byte[][], IEnumerable<BlobAndProofV1?>> handler) : SszEndpointHandlerBase
{
    private readonly IAsyncHandler<byte[][], IEnumerable<BlobAndProofV1?>> _handler = handler;

    public override string HttpMethod => "POST";
    public override string Resource => "blobs";
    public override int? Version => 1;

    public override async Task HandleAsync(HttpContext ctx, int version, string extra, byte[] body)
    {
        byte[][] hashes = SszCodec.DecodeGetBlobsRequest(body);
        await WriteSszResultAsync(ctx, await _handler.HandleAsync(hashes), SszCodec.EncodeGetBlobsV1Response);
    }
}
