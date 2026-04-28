// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;

namespace Nethermind.Merge.Plugin.SszRest.Handlers;

public sealed class GetBlobsV1SszHandler(
    IAsyncHandler<byte[][], IEnumerable<BlobAndProofV1?>> handler) : SszEndpointHandlerBase
{
    public override string HttpMethod => "POST";
    public override string Resource => "blobs";
    public override int? Version => 1;

    public override async Task HandleAsync(HttpContext ctx, int version, string extra, ReadOnlyMemory<byte> body)
    {
        byte[][] hashes = SszCodec.DecodeGetBlobsRequest(body.Span);
        await WriteSszResultAsync(ctx,
            await handler.HandleAsync(hashes),
            (IEnumerable<BlobAndProofV1?> e) =>
                SszCodec.EncodeGetBlobsV1Response(e as IReadOnlyList<BlobAndProofV1?> ?? e.ToList()));
    }
}

public sealed class GetBlobsV2SszHandler(
    int version,
    bool allowPartialReturn,
    IAsyncHandler<GetBlobsHandlerV2Request, IEnumerable<BlobAndProofV2?>?> handler,
    Func<IReadOnlyList<BlobAndProofV2?>, (byte[] buffer, int length)> encode) : SszEndpointHandlerBase
{
    public override string HttpMethod => "POST";
    public override string Resource => "blobs";
    public override int? Version => version;

    public override async Task HandleAsync(HttpContext ctx, int v, string extra, ReadOnlyMemory<byte> body)
    {
        byte[][] hashes = SszCodec.DecodeGetBlobsRequest(body.Span);
        await WriteSszResultAsync(ctx,
            await handler.HandleAsync(new GetBlobsHandlerV2Request(hashes, AllowPartialReturn: allowPartialReturn)),
            (IEnumerable<BlobAndProofV2?>? e) =>
                encode(e as IReadOnlyList<BlobAndProofV2?> ?? (e ?? []).ToList()));
    }
}
