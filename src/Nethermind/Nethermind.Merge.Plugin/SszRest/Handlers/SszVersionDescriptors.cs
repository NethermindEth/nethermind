// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Core.Collections;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;

namespace Nethermind.Merge.Plugin.SszRest.Handlers;

// This verbosity is deliberate: the static-abstract interface pattern requires one concrete struct
// per version so the JIT can devirtualise each call site.
public interface IGetPayloadVersion<TResult> where TResult : class
{
    static abstract int VersionNumber { get; }
    static abstract Task<ResultWrapper<TResult?>> Call(IEngineRpcModule engine, byte[] id);
    static abstract ArrayPoolSpan<byte> Encode(TResult result);
}

public readonly struct GetPayloadDescriptorV1 : IGetPayloadVersion<ExecutionPayload>
{
    public static int VersionNumber => 1;
    public static Task<ResultWrapper<ExecutionPayload?>> Call(IEngineRpcModule engine, byte[] id)
        => engine.engine_getPayloadV1(id);
    public static ArrayPoolSpan<byte> Encode(ExecutionPayload result)
        => SszCodec.EncodeGetPayloadV1Response(result);
}

public readonly struct GetPayloadDescriptorV2 : IGetPayloadVersion<GetPayloadV2Result>
{
    public static int VersionNumber => 2;
    public static Task<ResultWrapper<GetPayloadV2Result?>> Call(IEngineRpcModule engine, byte[] id)
        => engine.engine_getPayloadV2(id);
    public static ArrayPoolSpan<byte> Encode(GetPayloadV2Result result)
        => SszCodec.EncodeGetPayloadV2Response(result);
}

public readonly struct GetPayloadDescriptorV3 : IGetPayloadVersion<GetPayloadV3Result>
{
    public static int VersionNumber => 3;
    public static Task<ResultWrapper<GetPayloadV3Result?>> Call(IEngineRpcModule engine, byte[] id)
        => engine.engine_getPayloadV3(id);
    public static ArrayPoolSpan<byte> Encode(GetPayloadV3Result result)
        => SszCodec.EncodeGetPayloadV3Response(result);
}

public readonly struct GetPayloadDescriptorV4 : IGetPayloadVersion<GetPayloadV4Result>
{
    public static int VersionNumber => 4;
    public static Task<ResultWrapper<GetPayloadV4Result?>> Call(IEngineRpcModule engine, byte[] id)
        => engine.engine_getPayloadV4(id);
    public static ArrayPoolSpan<byte> Encode(GetPayloadV4Result result)
        => SszCodec.EncodeGetPayloadV4Response(result);
}

public readonly struct GetPayloadDescriptorV5 : IGetPayloadVersion<GetPayloadV5Result>
{
    public static int VersionNumber => 5;
    public static Task<ResultWrapper<GetPayloadV5Result?>> Call(IEngineRpcModule engine, byte[] id)
        => engine.engine_getPayloadV5(id);
    public static ArrayPoolSpan<byte> Encode(GetPayloadV5Result result)
        => SszCodec.EncodeGetPayloadV5Response(result);
}

public readonly struct GetPayloadDescriptorV6 : IGetPayloadVersion<GetPayloadV6Result>
{
    public static int VersionNumber => 6;
    public static Task<ResultWrapper<GetPayloadV6Result?>> Call(IEngineRpcModule engine, byte[] id)
        => engine.engine_getPayloadV6(id);
    public static ArrayPoolSpan<byte> Encode(GetPayloadV6Result result)
        => SszCodec.EncodeGetPayloadV6Response(result);
}

public interface IPayloadBodiesByHashVersion<TResult> where TResult : class
{
    static abstract int VersionNumber { get; }
    static abstract ArrayPoolSpan<byte> Encode(IReadOnlyList<TResult?> bodies);
}

public readonly struct PayloadBodiesByHashDescriptorV1 : IPayloadBodiesByHashVersion<ExecutionPayloadBodyV1Result>
{
    public static int VersionNumber => 1;
    public static ArrayPoolSpan<byte> Encode(IReadOnlyList<ExecutionPayloadBodyV1Result?> bodies)
        => SszCodec.EncodePayloadBodiesV1Response(bodies);
}

public readonly struct PayloadBodiesByHashDescriptorV2 : IPayloadBodiesByHashVersion<ExecutionPayloadBodyV2Result>
{
    public static int VersionNumber => 2;
    public static ArrayPoolSpan<byte> Encode(IReadOnlyList<ExecutionPayloadBodyV2Result?> bodies)
        => SszCodec.EncodePayloadBodiesV2Response(bodies);
}

public interface IPayloadBodiesByRangeVersion<TResult, THandler> where TResult : class
{
    static abstract int VersionNumber { get; }
    static abstract Task<ResultWrapper<IReadOnlyList<TResult?>>> Handle(THandler handler, long start, long count);
    static abstract ArrayPoolSpan<byte> Encode(IReadOnlyList<TResult?> bodies);
}

public readonly struct PayloadBodiesByRangeDescriptorV1
    : IPayloadBodiesByRangeVersion<ExecutionPayloadBodyV1Result, IGetPayloadBodiesByRangeV1Handler>
{
    public static int VersionNumber => 1;
    public static Task<ResultWrapper<IReadOnlyList<ExecutionPayloadBodyV1Result?>>> Handle(
        IGetPayloadBodiesByRangeV1Handler handler, long start, long count)
        => handler.Handle(start, count);
    public static ArrayPoolSpan<byte> Encode(IReadOnlyList<ExecutionPayloadBodyV1Result?> bodies)
        => SszCodec.EncodePayloadBodiesV1Response(bodies);
}

public readonly struct PayloadBodiesByRangeDescriptorV2
    : IPayloadBodiesByRangeVersion<ExecutionPayloadBodyV2Result, IGetPayloadBodiesByRangeV2Handler>
{
    public static int VersionNumber => 2;
    public static Task<ResultWrapper<IReadOnlyList<ExecutionPayloadBodyV2Result?>>> Handle(
        IGetPayloadBodiesByRangeV2Handler handler, long start, long count)
        => handler.Handle(start, count);
    public static ArrayPoolSpan<byte> Encode(IReadOnlyList<ExecutionPayloadBodyV2Result?> bodies)
        => SszCodec.EncodePayloadBodiesV2Response(bodies);
}

public interface IGetBlobsV2Version
{
    static abstract int VersionNumber { get; }
    static abstract bool AllowPartialReturn { get; }
    static abstract ArrayPoolSpan<byte> Encode(IReadOnlyList<BlobAndProofV2?> blobs);
}

public readonly struct GetBlobsDescriptorV2 : IGetBlobsV2Version
{
    public static int VersionNumber => 2;
    public static bool AllowPartialReturn => false;
    public static ArrayPoolSpan<byte> Encode(IReadOnlyList<BlobAndProofV2?> blobs)
        => SszCodec.EncodeGetBlobsV2Response(blobs);
}

public readonly struct GetBlobsDescriptorV3 : IGetBlobsV2Version
{
    public static int VersionNumber => 3;
    public static bool AllowPartialReturn => true;
    public static ArrayPoolSpan<byte> Encode(IReadOnlyList<BlobAndProofV2?> blobs)
        => SszCodec.EncodeGetBlobsV3Response(blobs);
}
