// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Nethermind.Core;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;

namespace Nethermind.Merge.Plugin.SszRest.Handlers;

public sealed class GetBlobsV1SszHandler(
    IAsyncHandler<byte[][], IReadOnlyList<BlobAndProofV1?>> handler) : SszEndpointHandlerBase
{
    public override string HttpMethod => "POST";
    public override string Resource => "blobs";
    public override int? Version => 1;

    public override async Task HandleAsync(HttpContext ctx, int version, string extra, ReadOnlyMemory<byte> body)
    {
        byte[][] hashes = SszCodec.DecodeGetBlobsRequest(body.Span);
        ResultWrapper<IReadOnlyList<BlobAndProofV1?>> result = await handler.HandleAsync(hashes);
        await WriteSszResultAsync(ctx, result, SszCodec.EncodeGetBlobsV1Response);
    }
}

public sealed class GetBlobsV2SszHandler<TVersion>(
    IAsyncHandler<GetBlobsHandlerV2Request, IReadOnlyList<BlobAndProofV2?>?> handler)
    : SszEndpointHandlerBase
    where TVersion : struct, IGetBlobsV2Version
{
    public override string HttpMethod => "POST";
    public override string Resource => "blobs";
    public override int? Version => TVersion.VersionNumber;

    public override async Task HandleAsync(HttpContext ctx, int v, string extra, ReadOnlyMemory<byte> body)
    {
        byte[][] hashes = SszCodec.DecodeGetBlobsRequest(body.Span);
        ResultWrapper<IReadOnlyList<BlobAndProofV2?>?> result = await handler.HandleAsync(
            new GetBlobsHandlerV2Request(hashes, AllowPartialReturn: TVersion.AllowPartialReturn));
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
        await WriteSszPooledAsync(ctx, TVersion.Encode(result.Data));
    }
}
