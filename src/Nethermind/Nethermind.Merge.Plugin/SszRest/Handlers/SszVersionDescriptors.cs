// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Consensus;
using Nethermind.Consensus.Producers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Serialization.Ssz;

namespace Nethermind.Merge.Plugin.SszRest.Handlers;

public interface IGetPayloadVersion<TResult> where TResult : class
{
    static abstract int VersionNumber { get; }
    static abstract Task<ResultWrapper<TResult?>> Call(IEngineRpcModule engine, byte[] id);
    static abstract ArrayPoolSpan<byte> Encode(TResult result);
}

public interface INewPayloadVersion<TWire> where TWire : struct, ISszCodec<TWire>
{
    static abstract int VersionNumber { get; }
    static abstract Task<ResultWrapper<PayloadStatusV1>> Call(IEngineRpcModule engine, in TWire wire);
}

public readonly struct NewPayloadDescriptorV1 : INewPayloadVersion<NewPayloadV1RequestWire>
{
    public static int VersionNumber => EngineApiVersions.NewPayload.V1;
    public static Task<ResultWrapper<PayloadStatusV1>> Call(IEngineRpcModule engine, in NewPayloadV1RequestWire wire)
        => engine.engine_newPayloadV1(wire.ExecutionPayload.Unwrap());
}

public readonly struct NewPayloadDescriptorV2 : INewPayloadVersion<NewPayloadV2RequestWire>
{
    public static int VersionNumber => EngineApiVersions.NewPayload.V2;
    public static Task<ResultWrapper<PayloadStatusV1>> Call(IEngineRpcModule engine, in NewPayloadV2RequestWire wire)
        => engine.engine_newPayloadV2(wire.ExecutionPayload.Unwrap());
}

public readonly struct NewPayloadDescriptorV3 : INewPayloadVersion<NewPayloadV3RequestWire>
{
    public static int VersionNumber => EngineApiVersions.NewPayload.V3;
    public static Task<ResultWrapper<PayloadStatusV1>> Call(IEngineRpcModule engine, in NewPayloadV3RequestWire wire)
        => engine.engine_newPayloadV3(
            wire.ExecutionPayload.Unwrap(),
            wire.ExpectedBlobVersionedHashes.ToBytesArrays(),
            wire.ParentBeaconBlockRoot);
}

public readonly struct NewPayloadDescriptorV4 : INewPayloadVersion<NewPayloadV4RequestWire>
{
    public static int VersionNumber => EngineApiVersions.NewPayload.V4;
    public static Task<ResultWrapper<PayloadStatusV1>> Call(IEngineRpcModule engine, in NewPayloadV4RequestWire wire)
        => engine.engine_newPayloadV4(
            wire.ExecutionPayload.Unwrap(),
            wire.ExpectedBlobVersionedHashes.ToBytesArrays(),
            wire.ParentBeaconBlockRoot,
            wire.ExecutionRequests.ToExecutionRequests());
}

public readonly struct NewPayloadDescriptorV5 : INewPayloadVersion<NewPayloadV5RequestWire>
{
    public static int VersionNumber => EngineApiVersions.NewPayload.V5;
    public static Task<ResultWrapper<PayloadStatusV1>> Call(IEngineRpcModule engine, in NewPayloadV5RequestWire wire)
        => engine.engine_newPayloadV5(
            wire.ExecutionPayload.Unwrap(),
            wire.ExpectedBlobVersionedHashes.ToBytesArrays(),
            wire.ParentBeaconBlockRoot,
            wire.ExecutionRequests.ToExecutionRequests());
}

public interface IForkchoiceUpdatedVersion<TWire> where TWire : struct, ISszCodec<TWire>
{
    static abstract int VersionNumber { get; }
    static abstract Task<ResultWrapper<ForkchoiceUpdatedV1Result>> Call(IEngineRpcModule engine, in TWire wire);
}

public readonly struct ForkchoiceUpdatedDescriptorV1 : IForkchoiceUpdatedVersion<ForkchoiceUpdatedV1RequestWire>
{
    public static int VersionNumber => EngineApiVersions.Fcu.V1;
    public static Task<ResultWrapper<ForkchoiceUpdatedV1Result>> Call(IEngineRpcModule engine, in ForkchoiceUpdatedV1RequestWire wire)
    {
        ForkchoiceStateV1 state = SszCodec.ForkchoiceStateV1FromWire(wire.ForkchoiceState);
        PayloadAttributes? attrs = wire.PayloadAttributes is { Length: > 0 } a ? SszCodec.PayloadAttributesFromWire(a[0]) : null;
        return engine.engine_forkchoiceUpdatedV1(state, attrs);
    }
}

public readonly struct ForkchoiceUpdatedDescriptorV2 : IForkchoiceUpdatedVersion<ForkchoiceUpdatedV2RequestWire>
{
    public static int VersionNumber => EngineApiVersions.Fcu.V2;
    public static Task<ResultWrapper<ForkchoiceUpdatedV1Result>> Call(IEngineRpcModule engine, in ForkchoiceUpdatedV2RequestWire wire)
    {
        ForkchoiceStateV1 state = SszCodec.ForkchoiceStateV1FromWire(wire.ForkchoiceState);
        PayloadAttributes? attrs = wire.PayloadAttributes is { Length: > 0 } a ? SszCodec.PayloadAttributesFromWire(a[0]) : null;
        return engine.engine_forkchoiceUpdatedV2(state, attrs);
    }
}

public readonly struct ForkchoiceUpdatedDescriptorV3 : IForkchoiceUpdatedVersion<ForkchoiceUpdatedV3RequestWire>
{
    public static int VersionNumber => EngineApiVersions.Fcu.V3;
    public static Task<ResultWrapper<ForkchoiceUpdatedV1Result>> Call(IEngineRpcModule engine, in ForkchoiceUpdatedV3RequestWire wire)
    {
        ForkchoiceStateV1 state = SszCodec.ForkchoiceStateV1FromWire(wire.ForkchoiceState);
        PayloadAttributes? attrs = wire.PayloadAttributes is { Length: > 0 } a ? SszCodec.PayloadAttributesFromWire(a[0]) : null;
        return engine.engine_forkchoiceUpdatedV3(state, attrs);
    }
}

public readonly struct ForkchoiceUpdatedDescriptorV4 : IForkchoiceUpdatedVersion<ForkchoiceUpdatedRequestWire>
{
    public static int VersionNumber => EngineApiVersions.Fcu.V4;
    public static Task<ResultWrapper<ForkchoiceUpdatedV1Result>> Call(IEngineRpcModule engine, in ForkchoiceUpdatedRequestWire wire)
    {
        ForkchoiceStateV1 state = SszCodec.ForkchoiceStateV1FromWire(wire.ForkchoiceState);
        PayloadAttributes? attrs = wire.PayloadAttributes is { Length: > 0 } a ? SszCodec.PayloadAttributesFromWire(a[0]) : null;
        return engine.engine_forkchoiceUpdatedV4(state, attrs);
    }
}

public readonly struct GetPayloadDescriptorV1 : IGetPayloadVersion<ExecutionPayload>
{
    public static int VersionNumber => EngineApiVersions.GetPayload.V1;
    public static Task<ResultWrapper<ExecutionPayload?>> Call(IEngineRpcModule engine, byte[] id)
        => engine.engine_getPayloadV1(id);
    public static ArrayPoolSpan<byte> Encode(ExecutionPayload result)
        => SszCodec.EncodeGetPayloadV1Response(result);
}

public readonly struct GetPayloadDescriptorV2 : IGetPayloadVersion<GetPayloadV2Result>
{
    public static int VersionNumber => EngineApiVersions.GetPayload.V2;
    public static Task<ResultWrapper<GetPayloadV2Result?>> Call(IEngineRpcModule engine, byte[] id)
        => engine.engine_getPayloadV2(id);
    public static ArrayPoolSpan<byte> Encode(GetPayloadV2Result result)
        => SszCodec.EncodeGetPayloadV2Response(result);
}

public readonly struct GetPayloadDescriptorV3 : IGetPayloadVersion<GetPayloadV3Result>
{
    public static int VersionNumber => EngineApiVersions.GetPayload.V3;
    public static Task<ResultWrapper<GetPayloadV3Result?>> Call(IEngineRpcModule engine, byte[] id)
        => engine.engine_getPayloadV3(id);
    public static ArrayPoolSpan<byte> Encode(GetPayloadV3Result result)
        => SszCodec.EncodeGetPayloadV3Response(result);
}

public readonly struct GetPayloadDescriptorV4 : IGetPayloadVersion<GetPayloadV4Result>
{
    public static int VersionNumber => EngineApiVersions.GetPayload.V4;
    public static Task<ResultWrapper<GetPayloadV4Result?>> Call(IEngineRpcModule engine, byte[] id)
        => engine.engine_getPayloadV4(id);
    public static ArrayPoolSpan<byte> Encode(GetPayloadV4Result result)
        => SszCodec.EncodeGetPayloadV4Response(result);
}

public readonly struct GetPayloadDescriptorV5 : IGetPayloadVersion<GetPayloadV5Result>
{
    public static int VersionNumber => EngineApiVersions.GetPayload.V5;
    public static Task<ResultWrapper<GetPayloadV5Result?>> Call(IEngineRpcModule engine, byte[] id)
        => engine.engine_getPayloadV5(id);
    public static ArrayPoolSpan<byte> Encode(GetPayloadV5Result result)
        => SszCodec.EncodeGetPayloadV5Response(result);
}

public readonly struct GetPayloadDescriptorV6 : IGetPayloadVersion<GetPayloadV6Result>
{
    public static int VersionNumber => EngineApiVersions.GetPayload.V6;
    public static Task<ResultWrapper<GetPayloadV6Result?>> Call(IEngineRpcModule engine, byte[] id)
        => engine.engine_getPayloadV6(id);
    public static ArrayPoolSpan<byte> Encode(GetPayloadV6Result result)
        => SszCodec.EncodeGetPayloadV6Response(result);
}

public interface IPayloadBodiesByHashVersion<TResult> where TResult : class
{
    static abstract int VersionNumber { get; }
    static abstract Task<ResultWrapper<IReadOnlyList<TResult?>>> Call(IEngineRpcModule engine, IReadOnlyList<Hash256> hashes);
    static abstract ArrayPoolSpan<byte> Encode(IReadOnlyList<TResult?> bodies);
}

public readonly struct PayloadBodiesByHashDescriptorV1 : IPayloadBodiesByHashVersion<ExecutionPayloadBodyV1Result>
{
    public static int VersionNumber => EngineApiVersions.PayloadBodiesByHash.V1;
    public static Task<ResultWrapper<IReadOnlyList<ExecutionPayloadBodyV1Result?>>> Call(IEngineRpcModule engine, IReadOnlyList<Hash256> hashes)
        => Task.FromResult(engine.engine_getPayloadBodiesByHashV1(hashes));
    public static ArrayPoolSpan<byte> Encode(IReadOnlyList<ExecutionPayloadBodyV1Result?> bodies)
        => SszCodec.EncodePayloadBodiesV1Response(bodies);
}

public readonly struct PayloadBodiesByHashDescriptorV2 : IPayloadBodiesByHashVersion<ExecutionPayloadBodyV2Result>
{
    public static int VersionNumber => EngineApiVersions.PayloadBodiesByHash.V2;
    public static Task<ResultWrapper<IReadOnlyList<ExecutionPayloadBodyV2Result?>>> Call(IEngineRpcModule engine, IReadOnlyList<Hash256> hashes)
        => engine.engine_getPayloadBodiesByHashV2(hashes);
    public static ArrayPoolSpan<byte> Encode(IReadOnlyList<ExecutionPayloadBodyV2Result?> bodies)
        => SszCodec.EncodePayloadBodiesV2Response(bodies);
}

public interface IPayloadBodiesByRangeVersion<TResult> where TResult : class
{
    static abstract int VersionNumber { get; }
    static abstract Task<ResultWrapper<IReadOnlyList<TResult?>>> Call(IEngineRpcModule engine, long start, long count);
    static abstract ArrayPoolSpan<byte> Encode(IReadOnlyList<TResult?> bodies);
}

public readonly struct PayloadBodiesByRangeDescriptorV1 : IPayloadBodiesByRangeVersion<ExecutionPayloadBodyV1Result>
{
    public static int VersionNumber => EngineApiVersions.PayloadBodiesByRange.V1;
    public static Task<ResultWrapper<IReadOnlyList<ExecutionPayloadBodyV1Result?>>> Call(IEngineRpcModule engine, long start, long count)
        => engine.engine_getPayloadBodiesByRangeV1(start, count);
    public static ArrayPoolSpan<byte> Encode(IReadOnlyList<ExecutionPayloadBodyV1Result?> bodies)
        => SszCodec.EncodePayloadBodiesV1Response(bodies);
}

public readonly struct PayloadBodiesByRangeDescriptorV2 : IPayloadBodiesByRangeVersion<ExecutionPayloadBodyV2Result>
{
    public static int VersionNumber => EngineApiVersions.PayloadBodiesByRange.V2;
    public static Task<ResultWrapper<IReadOnlyList<ExecutionPayloadBodyV2Result?>>> Call(IEngineRpcModule engine, long start, long count)
        => engine.engine_getPayloadBodiesByRangeV2(start, count);
    public static ArrayPoolSpan<byte> Encode(IReadOnlyList<ExecutionPayloadBodyV2Result?> bodies)
        => SszCodec.EncodePayloadBodiesV2Response(bodies);
}

public interface IGetBlobsV2Version
{
    static abstract int VersionNumber { get; }
    static abstract Task<ResultWrapper<IReadOnlyList<BlobAndProofV2?>?>> Call(IEngineRpcModule engine, byte[][] hashes);
    static abstract ArrayPoolSpan<byte> Encode(IReadOnlyList<BlobAndProofV2?> blobs);
}

public readonly struct GetBlobsDescriptorV2 : IGetBlobsV2Version
{
    public static int VersionNumber => EngineApiVersions.GetBlobs.V2;
    public static Task<ResultWrapper<IReadOnlyList<BlobAndProofV2?>?>> Call(IEngineRpcModule engine, byte[][] hashes)
        => engine.engine_getBlobsV2(hashes);
    public static ArrayPoolSpan<byte> Encode(IReadOnlyList<BlobAndProofV2?> blobs)
        => SszCodec.EncodeGetBlobsV2Response(blobs);
}

public readonly struct GetBlobsDescriptorV3 : IGetBlobsV2Version
{
    public static int VersionNumber => EngineApiVersions.GetBlobs.V3;
    public static Task<ResultWrapper<IReadOnlyList<BlobAndProofV2?>?>> Call(IEngineRpcModule engine, byte[][] hashes)
        => engine.engine_getBlobsV3(hashes);
    public static ArrayPoolSpan<byte> Encode(IReadOnlyList<BlobAndProofV2?> blobs)
        => SszCodec.EncodeGetBlobsV3Response(blobs);
}
