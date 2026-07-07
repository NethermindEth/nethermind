// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Nethermind.Core;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Merge.Plugin.SszRest.Handlers;

public sealed class GetBlobsV1SszHandler(IEngineRpcModule engineModule) : SszEndpointHandlerBase
{
    public override string HttpMethod => "POST";
    public override string Resource => SszRestPaths.Blobs;
    public override int? Version => EngineApiVersions.GetBlobs.V1;

    public override async Task HandleAsync(HttpContext ctx, int version, ReadOnlyMemory<char> extra, ReadOnlySequence<byte> body)
    {
        byte[][] hashes = SszCodec.DecodeGetBlobsRequest(body);
        ResultWrapper<IReadOnlyList<BlobAndProofV1?>> result = await engineModule.engine_getBlobsV1(hashes);
        await WriteSszResultAsync(ctx, result, SszCodec.EncodeGetBlobsV1Response);
    }
}

public sealed class GetBlobsV2SszHandler<TVersion>(IEngineRpcModule engineModule)
    : SszEndpointHandlerBase
    where TVersion : struct, IGetBlobsV2Version
{
    public override string HttpMethod => "POST";
    public override string Resource => SszRestPaths.Blobs;
    public override int? Version => TVersion.VersionNumber;

    public override async Task HandleAsync(HttpContext ctx, int v, ReadOnlyMemory<char> extra, ReadOnlySequence<byte> body)
    {
        byte[][] hashes = SszCodec.DecodeGetBlobsRequest(body);
        ResultWrapper<IReadOnlyList<BlobAndProofV2?>?> result = await TVersion.Call(engineModule, hashes);
        await WriteSszResultAsync(ctx, result, static (d, w) => TVersion.Encode(d!, w));
    }
}

public sealed class GetBlobsV4SszHandler(IEngineRpcModule engineModule) : SszEndpointHandlerBase
{
    public override string HttpMethod => "POST";
    public override string Resource => SszRestPaths.Blobs;
    public override int? Version => EngineApiVersions.GetBlobs.V4;

    public override async Task HandleAsync(HttpContext ctx, int version, ReadOnlyMemory<char> extra, ReadOnlySequence<byte> body)
    {
        (byte[][] hashes, System.Collections.BitArray indices) = SszCodec.DecodeGetBlobsV4Request(body);
        ResultWrapper<IReadOnlyList<BlobCellsAndProofs?>?> result = await engineModule.engine_getBlobsV4(hashes, SszCodec.EncodeBitArray(indices));
        await WriteSszResultAsync(ctx, result, static (d, w) => SszCodec.EncodeGetBlobsV4Response(d!, w));
    }
}
