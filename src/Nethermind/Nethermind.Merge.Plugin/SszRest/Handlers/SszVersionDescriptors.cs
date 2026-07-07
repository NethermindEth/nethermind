// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Consensus.Producers;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Serialization.Ssz;

namespace Nethermind.Merge.Plugin.SszRest.Handlers;

public interface IGetPayloadVersion<TResult> where TResult : class
{
    static abstract int VersionNumber { get; }
    static abstract Task<ResultWrapper<TResult?>> Call(IEngineRpcModule engine, byte[] id);
    static abstract int Encode(TResult result, IBufferWriter<byte> writer);
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
        => engine.engine_newPayloadV1(wire.ExecutionPayload.AsExecutionPayload());
}

public readonly struct NewPayloadDescriptorV2 : INewPayloadVersion<NewPayloadV2RequestWire>
{
    public static int VersionNumber => EngineApiVersions.NewPayload.V2;
    public static Task<ResultWrapper<PayloadStatusV1>> Call(IEngineRpcModule engine, in NewPayloadV2RequestWire wire)
        => engine.engine_newPayloadV2(wire.ExecutionPayload.AsExecutionPayload());
}

public readonly struct NewPayloadDescriptorV3 : INewPayloadVersion<NewPayloadV3RequestWire>
{
    public static int VersionNumber => EngineApiVersions.NewPayload.V3;
    public static Task<ResultWrapper<PayloadStatusV1>> Call(IEngineRpcModule engine, in NewPayloadV3RequestWire wire)
    {
        ExecutionPayloadV3 ep = wire.ExecutionPayload.AsExecutionPayload();
        return engine.engine_newPayloadV3(ep, SszCodec.GetBlobVersionedHashes(ep), wire.ParentBeaconBlockRoot);
    }
}

public readonly struct NewPayloadDescriptorV4 : INewPayloadVersion<NewPayloadV4RequestWire>
{
    public static int VersionNumber => EngineApiVersions.NewPayload.V4;
    public static Task<ResultWrapper<PayloadStatusV1>> Call(IEngineRpcModule engine, in NewPayloadV4RequestWire wire)
    {
        ExecutionPayloadV3 ep = wire.ExecutionPayload.AsExecutionPayload();
        return engine.engine_newPayloadV4(ep, SszCodec.GetBlobVersionedHashes(ep), wire.ParentBeaconBlockRoot, wire.ExecutionRequests.ToExecutionRequests());
    }
}

public readonly struct NewPayloadDescriptorV5 : INewPayloadVersion<NewPayloadV5RequestWire>
{
    public static int VersionNumber => EngineApiVersions.NewPayload.V5;
    public static Task<ResultWrapper<PayloadStatusV1>> Call(IEngineRpcModule engine, in NewPayloadV5RequestWire wire)
    {
        ExecutionPayloadV4 ep = wire.ExecutionPayload.AsExecutionPayload();
        return engine.engine_newPayloadV5(ep, SszCodec.GetBlobVersionedHashes(ep), wire.ParentBeaconBlockRoot, wire.ExecutionRequests.ToExecutionRequests());
    }
}

public interface INewPayloadWithWitnessVersion<TWire> where TWire : struct, ISszCodec<TWire>
{
    static abstract int VersionNumber { get; }
    static abstract Task<ResultWrapper<NewPayloadWithWitnessV1Result>> Call(IEngineRpcModule engine, in TWire wire);
}

public readonly struct NewPayloadWithWitnessDescriptorV1 : INewPayloadWithWitnessVersion<NewPayloadV5RequestWire>
{
    public static int VersionNumber => EngineApiVersions.NewPayload.V5;
    public static Task<ResultWrapper<NewPayloadWithWitnessV1Result>> Call(IEngineRpcModule engine, in NewPayloadV5RequestWire wire)
    {
        ExecutionPayloadV4 ep = wire.ExecutionPayload.AsExecutionPayload();
        return engine.engine_newPayloadWithWitness(ep, SszCodec.GetBlobVersionedHashes(ep), wire.ParentBeaconBlockRoot, wire.ExecutionRequests.ToExecutionRequests());
    }
}

public interface IForkchoiceUpdatedVersion<TWire> where TWire : struct, ISszCodec<TWire>
{
    static abstract int VersionNumber { get; }
    static abstract Task<ResultWrapper<ForkchoiceUpdatedV1Result>> Call(IEngineRpcModule engine, in TWire wire);
    static abstract ulong? GetTimestamp(in TWire wire);
}

/// <summary>
/// Helpers shared by all <c>ForkchoiceUpdated</c> descriptors.
/// </summary>
internal static class ForkchoiceUpdatedHelpers
{
    /// <summary>First-element timestamp of an optional payload-attributes wire list, or <c>null</c>.</summary>
    public static ulong? FirstTimestamp<TAttr>(TAttr[]? attrs)
        where TAttr : struct, ISszPayloadAttributesWire
        => attrs is { Length: > 0 } a ? a[0].Timestamp : null;
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
    public static ulong? GetTimestamp(in ForkchoiceUpdatedV1RequestWire wire) =>
        ForkchoiceUpdatedHelpers.FirstTimestamp(wire.PayloadAttributes);
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
    public static ulong? GetTimestamp(in ForkchoiceUpdatedV2RequestWire wire) =>
        ForkchoiceUpdatedHelpers.FirstTimestamp(wire.PayloadAttributes);
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
    public static ulong? GetTimestamp(in ForkchoiceUpdatedV3RequestWire wire) =>
        ForkchoiceUpdatedHelpers.FirstTimestamp(wire.PayloadAttributes);
}

public readonly struct ForkchoiceUpdatedDescriptorV4 : IForkchoiceUpdatedVersion<ForkchoiceUpdatedRequestWire>
{
    public static int VersionNumber => EngineApiVersions.Fcu.V4;
    public static Task<ResultWrapper<ForkchoiceUpdatedV1Result>> Call(IEngineRpcModule engine, in ForkchoiceUpdatedRequestWire wire)
    {
        ForkchoiceStateV1 state = SszCodec.ForkchoiceStateV1FromWire(wire.ForkchoiceState);
        PayloadAttributes? attrs = wire.PayloadAttributes is { Length: > 0 } a ? SszCodec.PayloadAttributesFromWire(a[0]) : null;
        BitArray? custody = wire.CustodyColumns is { Length: > 0 } c ? c[0].Bits : null;
        return engine.engine_forkchoiceUpdatedV4(state, attrs, custody);
    }
    public static ulong? GetTimestamp(in ForkchoiceUpdatedRequestWire wire) =>
        ForkchoiceUpdatedHelpers.FirstTimestamp(wire.PayloadAttributes);
}

// Paris (V1 routing) needs block_value, which engine_getPayloadV1 doesn't return — call V2.
public readonly struct GetPayloadDescriptorV1 : IGetPayloadVersion<GetPayloadV2Result>
{
    public static int VersionNumber => EngineApiVersions.GetPayload.V1;
    public static Task<ResultWrapper<GetPayloadV2Result?>> Call(IEngineRpcModule engine, byte[] id)
        => engine.engine_getPayloadV2(id);
    public static int Encode(GetPayloadV2Result result, IBufferWriter<byte> writer)
        => SszCodec.EncodeBuiltPayloadParis(result, writer);
}

public readonly struct GetPayloadDescriptorV2 : IGetPayloadVersion<GetPayloadV2Result>
{
    public static int VersionNumber => EngineApiVersions.GetPayload.V2;
    public static Task<ResultWrapper<GetPayloadV2Result?>> Call(IEngineRpcModule engine, byte[] id)
        => engine.engine_getPayloadV2(id);
    public static int Encode(GetPayloadV2Result result, IBufferWriter<byte> writer)
        => SszCodec.EncodeGetPayloadV2Response(result, writer);
}

public readonly struct GetPayloadDescriptorV3 : IGetPayloadVersion<GetPayloadV3Result>
{
    public static int VersionNumber => EngineApiVersions.GetPayload.V3;
    public static Task<ResultWrapper<GetPayloadV3Result?>> Call(IEngineRpcModule engine, byte[] id)
        => engine.engine_getPayloadV3(id);
    public static int Encode(GetPayloadV3Result result, IBufferWriter<byte> writer)
        => SszCodec.EncodeGetPayloadV3Response(result, writer);
}

public readonly struct GetPayloadDescriptorV4 : IGetPayloadVersion<GetPayloadV4Result>
{
    public static int VersionNumber => EngineApiVersions.GetPayload.V4;
    public static Task<ResultWrapper<GetPayloadV4Result?>> Call(IEngineRpcModule engine, byte[] id)
        => engine.engine_getPayloadV4(id);
    public static int Encode(GetPayloadV4Result result, IBufferWriter<byte> writer)
        => SszCodec.EncodeGetPayloadV4Response(result, writer);
}

public readonly struct GetPayloadDescriptorV5 : IGetPayloadVersion<GetPayloadV5Result>
{
    public static int VersionNumber => EngineApiVersions.GetPayload.V5;
    public static Task<ResultWrapper<GetPayloadV5Result?>> Call(IEngineRpcModule engine, byte[] id)
        => engine.engine_getPayloadV5(id);
    public static int Encode(GetPayloadV5Result result, IBufferWriter<byte> writer)
        => SszCodec.EncodeGetPayloadV5Response(result, writer);
}

public readonly struct GetPayloadDescriptorV6 : IGetPayloadVersion<GetPayloadV6Result>
{
    public static int VersionNumber => EngineApiVersions.GetPayload.V6;
    public static Task<ResultWrapper<GetPayloadV6Result?>> Call(IEngineRpcModule engine, byte[] id)
        => engine.engine_getPayloadV6(id);
    public static int Encode(GetPayloadV6Result result, IBufferWriter<byte> writer)
        => SszCodec.EncodeGetPayloadV6Response(result, writer);
}

public interface IPayloadBodiesByHashVersion<TResult> where TResult : class
{
    static abstract int VersionNumber { get; }
    static abstract Task<ResultWrapper<IReadOnlyList<TResult?>>> Call(IEngineRpcModule engine, IReadOnlyList<Hash256> hashes);
    static abstract int Encode(IReadOnlyList<TResult?> bodies, IBufferWriter<byte> writer);
}

public readonly struct PayloadBodiesByHashDescriptorV1 : IPayloadBodiesByHashVersion<ExecutionPayloadBodyV1Result>
{
    public static int VersionNumber => EngineApiVersions.PayloadBodiesByHash.V1;
    public static Task<ResultWrapper<IReadOnlyList<ExecutionPayloadBodyV1Result?>>> Call(IEngineRpcModule engine, IReadOnlyList<Hash256> hashes)
        => Task.FromResult(engine.engine_getPayloadBodiesByHashV1(hashes));
    public static int Encode(IReadOnlyList<ExecutionPayloadBodyV1Result?> bodies, IBufferWriter<byte> writer)
        => SszCodec.EncodePayloadBodiesV1Response(bodies, writer);
}

public readonly struct PayloadBodiesByHashDescriptorV2 : IPayloadBodiesByHashVersion<ExecutionPayloadBodyV2Result>
{
    public static int VersionNumber => EngineApiVersions.PayloadBodiesByHash.V2;
    public static Task<ResultWrapper<IReadOnlyList<ExecutionPayloadBodyV2Result?>>> Call(IEngineRpcModule engine, IReadOnlyList<Hash256> hashes)
        => engine.engine_getPayloadBodiesByHashV2(hashes);
    public static int Encode(IReadOnlyList<ExecutionPayloadBodyV2Result?> bodies, IBufferWriter<byte> writer)
        => SszCodec.EncodePayloadBodiesV2Response(bodies, writer);
}

public interface IPayloadBodiesByRangeVersion<TResult> where TResult : class
{
    static abstract int VersionNumber { get; }
    static abstract Task<ResultWrapper<IReadOnlyList<TResult?>>> Call(IEngineRpcModule engine, ulong start, ulong count);
    static abstract int Encode(IReadOnlyList<TResult?> bodies, IBufferWriter<byte> writer);
}

public readonly struct PayloadBodiesByRangeDescriptorV1 : IPayloadBodiesByRangeVersion<ExecutionPayloadBodyV1Result>
{
    public static int VersionNumber => EngineApiVersions.PayloadBodiesByRange.V1;
    public static Task<ResultWrapper<IReadOnlyList<ExecutionPayloadBodyV1Result?>>> Call(IEngineRpcModule engine, ulong start, ulong count)
        => engine.engine_getPayloadBodiesByRangeV1(start, count);
    public static int Encode(IReadOnlyList<ExecutionPayloadBodyV1Result?> bodies, IBufferWriter<byte> writer)
        => SszCodec.EncodePayloadBodiesV1Response(bodies, writer);
}

public readonly struct PayloadBodiesByRangeDescriptorV2 : IPayloadBodiesByRangeVersion<ExecutionPayloadBodyV2Result>
{
    public static int VersionNumber => EngineApiVersions.PayloadBodiesByRange.V2;
    public static Task<ResultWrapper<IReadOnlyList<ExecutionPayloadBodyV2Result?>>> Call(IEngineRpcModule engine, ulong start, ulong count)
        => engine.engine_getPayloadBodiesByRangeV2(start, count);
    public static int Encode(IReadOnlyList<ExecutionPayloadBodyV2Result?> bodies, IBufferWriter<byte> writer)
        => SszCodec.EncodePayloadBodiesV2Response(bodies, writer);
}

public interface IGetBlobsV2Version
{
    static abstract int VersionNumber { get; }
    static abstract Task<ResultWrapper<IReadOnlyList<BlobAndProofV2?>?>> Call(IEngineRpcModule engine, byte[][] hashes);
    static abstract int Encode(IReadOnlyList<BlobAndProofV2?> blobs, IBufferWriter<byte> writer);
}

public readonly struct GetBlobsDescriptorV2 : IGetBlobsV2Version
{
    public static int VersionNumber => EngineApiVersions.GetBlobs.V2;
    public static Task<ResultWrapper<IReadOnlyList<BlobAndProofV2?>?>> Call(IEngineRpcModule engine, byte[][] hashes)
        => engine.engine_getBlobsV2(hashes);
    public static int Encode(IReadOnlyList<BlobAndProofV2?> blobs, IBufferWriter<byte> writer)
        => SszCodec.EncodeGetBlobsV2Response(blobs, writer);
}

public readonly struct GetBlobsDescriptorV3 : IGetBlobsV2Version
{
    public static int VersionNumber => EngineApiVersions.GetBlobs.V3;
    public static Task<ResultWrapper<IReadOnlyList<BlobAndProofV2?>?>> Call(IEngineRpcModule engine, byte[][] hashes)
        => engine.engine_getBlobsV3(hashes);
    public static int Encode(IReadOnlyList<BlobAndProofV2?> blobs, IBufferWriter<byte> writer)
        => SszCodec.EncodeGetBlobsV3Response(blobs, writer);
}

public interface IGetBlobsV4Version
{
    static abstract int VersionNumber { get; }
    static abstract Task<ResultWrapper<IReadOnlyList<BlobCellsAndProofs?>?>> Call(IEngineRpcModule engine, byte[][] hashes, System.Collections.BitArray indicesBitarray);
    static abstract int Encode(IReadOnlyList<BlobCellsAndProofs?> blobs, IBufferWriter<byte> writer);
}

public readonly struct GetBlobsDescriptorV4 : IGetBlobsV4Version
{
    public static int VersionNumber => EngineApiVersions.GetBlobs.V4;
    public static Task<ResultWrapper<IReadOnlyList<BlobCellsAndProofs?>?>> Call(IEngineRpcModule engine, byte[][] hashes, System.Collections.BitArray indicesBitarray)
        => engine.engine_getBlobsV4(hashes, SszCodec.EncodeBitArray(indicesBitarray));
    public static int Encode(IReadOnlyList<BlobCellsAndProofs?> blobs, IBufferWriter<byte> writer)
        => SszCodec.EncodeGetBlobsV4Response(blobs, writer);
}
