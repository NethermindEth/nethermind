// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Nethermind.JsonRpc;
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
        ResultWrapper<IEnumerable<BlobAndProofV1?>> result = await handler.HandleAsync(hashes);
        await WriteSszResultAsync(ctx, result, static e =>
        {
            IReadOnlyList<BlobAndProofV1?> list = e as IReadOnlyList<BlobAndProofV1?> ?? AsReadOnlyList(e);
            return SszCodec.EncodeGetBlobsV1Response(list);
        });
    }
}

public sealed class GetBlobsV2SszHandler<TVersion>(
    IAsyncHandler<GetBlobsHandlerV2Request, IEnumerable<BlobAndProofV2?>?> handler)
    : SszEndpointHandlerBase
    where TVersion : struct, IGetBlobsV2Version
{
    public override string HttpMethod => "POST";
    public override string Resource => "blobs";
    public override int? Version => TVersion.VersionNumber;

    public override async Task HandleAsync(HttpContext ctx, int v, string extra, ReadOnlyMemory<byte> body)
    {
        byte[][] hashes = SszCodec.DecodeGetBlobsRequest(body.Span);
        IEnumerable<BlobAndProofV2?>? data =
            (IEnumerable<BlobAndProofV2?>?)await handler.HandleAsync(new GetBlobsHandlerV2Request(hashes, AllowPartialReturn: TVersion.AllowPartialReturn));
        IReadOnlyList<BlobAndProofV2?> list = data as IReadOnlyList<BlobAndProofV2?> ?? AsReadOnlyList(data ?? []);
        await WriteSszPooledAsync(ctx, TVersion.Encode(list));
    }
}
