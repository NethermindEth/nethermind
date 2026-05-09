// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Consensus.Producers;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Serialization.Ssz;

namespace Nethermind.Merge.Plugin.SszRest;

public static class SszCodec
{
    /// <summary>
    /// SSZ-encodes <paramref name="value"/> directly into <paramref name="writer"/>'s buffer
    /// (no intermediate pooled allocation) and returns the number of bytes written.
    /// </summary>
    private static int EncodeToWriter<T>(T value, IBufferWriter<byte> writer) where T : ISszCodec<T>
    {
        int length = T.GetLength(value);
        Span<byte> dst = writer.GetSpan(length)[..length];
        T.Encode(dst, value);
        writer.Advance(length);
        return length;
    }

    public static int EncodePayloadStatus(PayloadStatusV1 ps, IBufferWriter<byte> writer)
        => EncodeToWriter(BuildPayloadStatusWire(ps), writer);

    public static int EncodeForkchoiceUpdatedResponse(ForkchoiceUpdatedV1Result resp, IBufferWriter<byte> writer)
    {
        SszBytes8[]? pidList = null;
        if (resp.PayloadId is not null)
        {
            ReadOnlySpan<char> hex = resp.PayloadId.AsSpan();
            if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) hex = hex[2..];
            if (hex.Length != 16)
                throw new InvalidOperationException($"Invalid payload id '{resp.PayloadId}': expected 16 hex chars, got {hex.Length}");
            Span<byte> stack = stackalloc byte[8];
            Bytes.FromHexString(hex, stack);
            pidList = [SszBytes8.FromSpan(stack)];
        }

        return EncodeToWriter(new ForkchoiceUpdatedResponseWire
        {
            PayloadStatus = BuildPayloadStatusWire(resp.PayloadStatus),
            PayloadId = pidList ?? []
        }, writer);
    }

    internal static ForkchoiceStateV1 ForkchoiceStateV1FromWire(ForkchoiceStateWire w) => new(
        headBlockHash: w.HeadBlockHash,
        finalizedBlockHash: w.FinalizedBlockHash,
        safeBlockHash: w.SafeBlockHash);


    public static int EncodeGetPayloadV1Response(ExecutionPayload ep, IBufferWriter<byte> writer)
        => EncodeToWriter(new SszExecutionPayloadV1(ep), writer);

    public static int EncodeGetPayloadV2Response(GetPayloadV2Result? r, IBufferWriter<byte> writer)
        => EncodeToWriter(new GetPayloadResponseV2Wire
        {
            ExecutionPayload = new SszExecutionPayload(r!.ExecutionPayload),
            BlockValue = r.BlockValue
        }, writer);

    public static int EncodeGetPayloadV3Response(GetPayloadV3Result? r, IBufferWriter<byte> writer)
        => EncodeToWriter(new GetPayloadResponseV3Wire
        {
            ExecutionPayload = new SszExecutionPayloadV3((ExecutionPayloadV3)r!.ExecutionPayload),
            BlockValue = r.BlockValue,
            BlobsBundle = r.BlobsBundle.ToWire(),
            ShouldOverrideBuilder = r.ShouldOverrideBuilder
        }, writer);

    public static int EncodeGetPayloadV4Response(GetPayloadV4Result? r, IBufferWriter<byte> writer)
        => EncodeToWriter(new GetPayloadResponseV4Wire
        {
            ExecutionPayload = new SszExecutionPayloadV3((ExecutionPayloadV3)r!.ExecutionPayload),
            BlockValue = r.BlockValue,
            BlobsBundle = r.BlobsBundle.ToWire(),
            ShouldOverrideBuilder = r.ShouldOverrideBuilder,
            ExecutionRequests = r.ExecutionRequests.ToExecutionRequestsWire()
        }, writer);

    public static int EncodeGetPayloadV5Response(GetPayloadV5Result? r, IBufferWriter<byte> writer)
        => EncodeToWriter(new GetPayloadResponseV5Wire
        {
            ExecutionPayload = new SszExecutionPayloadV3((ExecutionPayloadV3)r!.ExecutionPayload),
            BlockValue = r.BlockValue,
            BlobsBundle = r.BlobsBundle.ToWire(),
            ShouldOverrideBuilder = r.ShouldOverrideBuilder,
            ExecutionRequests = r.ExecutionRequests.ToExecutionRequestsWire()
        }, writer);

    public static int EncodeGetPayloadV6Response(GetPayloadV6Result? r, IBufferWriter<byte> writer)
        => EncodeToWriter(new GetPayloadResponseV6Wire
        {
            ExecutionPayload = new SszExecutionPayloadV4((ExecutionPayloadV4)r!.ExecutionPayload),
            BlockValue = r.BlockValue,
            BlobsBundle = r.BlobsBundle.ToWire(),
            ShouldOverrideBuilder = r.ShouldOverrideBuilder,
            ExecutionRequests = r.ExecutionRequests.ToExecutionRequestsWire()
        }, writer);

    public static byte[][] DecodeGetBlobsRequest(ReadOnlySequence<byte> buf)
    {
        GetBlobsRequestWire.Decode(buf, out GetBlobsRequestWire wire);
        if (wire.VersionedHashes is null) return [];
        byte[][] result = new byte[wire.VersionedHashes.Length][];
        for (int i = 0; i < result.Length; i++)
            result[i] = wire.VersionedHashes[i].Bytes.ToArray();
        return result;
    }

    public static int EncodeGetBlobsV1Response(IReadOnlyList<BlobAndProofV1?> blobs, IBufferWriter<byte> writer)
    {
        // V1 SSZ has no nullable wrapper, nulls (unknown hashes) are dropped and the CL
        // infers misses by comparing response length to request length.
        int count = blobs.Count;
        int filled = 0;
        for (int i = 0; i < count; i++) if (blobs[i] is not null) filled++;
        BlobAndProofV1Wire[] arr = new BlobAndProofV1Wire[filled];
        int j = 0;
        for (int i = 0; i < count; i++)
            if (blobs[i] is { } b) arr[j++] = new() { Blob = b.Blob, Proof = b.Proof };
        return EncodeToWriter(new GetBlobsV1ResponseWire { BlobsAndProofs = arr }, writer);
    }

    public static int EncodeGetBlobsV2Response(IReadOnlyList<BlobAndProofV2?> blobs, IBufferWriter<byte> writer)
    {
        int count = blobs.Count;
        int filled = 0;
        for (int i = 0; i < count; i++) if (blobs[i] is not null) filled++;
        BlobAndProofV2Wire[] arr = new BlobAndProofV2Wire[filled];
        int j = 0;
        for (int i = 0; i < count; i++)
            if (blobs[i] is { } b) arr[j++] = new() { Blob = b.Blob, Proofs = b.Proofs.ToKzgWire() };
        return EncodeToWriter(new GetBlobsV2ResponseWire { BlobsAndProofs = arr }, writer);
    }

    public static int EncodeGetBlobsV3Response(IReadOnlyList<BlobAndProofV2?> blobs, IBufferWriter<byte> writer)
    {
        int count = blobs.Count;
        NullableBlobAndProofV2Wire[] arr = new NullableBlobAndProofV2Wire[count];
        for (int i = 0; i < count; i++)
        {
            BlobAndProofV2? b = blobs[i];
            arr[i] = b is null
                ? new() { BlobAndProof = [] }
                : new() { BlobAndProof = [new() { Blob = b.Blob, Proofs = b.Proofs.ToKzgWire() }] };
        }
        return EncodeToWriter(new GetBlobsV3ResponseWire { BlobsAndProofs = arr }, writer);
    }

    public static Hash256[] DecodeGetPayloadBodiesByHashRequest(ReadOnlySequence<byte> buf)
    {
        GetPayloadBodiesByHashRequestWire.Decode(buf, out GetPayloadBodiesByHashRequestWire wire);
        return wire.BlockHashes ?? [];
    }

    public static (long start, long count) DecodeGetPayloadBodiesByRangeRequest(ReadOnlySequence<byte> buf)
    {
        GetPayloadBodiesByRangeRequestWire.Decode(buf, out GetPayloadBodiesByRangeRequestWire wire);
        return ((long)wire.Start, (long)wire.Count);
    }

    public static int EncodePayloadBodiesV1Response(IReadOnlyList<ExecutionPayloadBodyV1Result?> bodies, IBufferWriter<byte> writer)
    {
        int count = bodies.Count;
        NullablePayloadBodyV1Wire[] arr = new NullablePayloadBodyV1Wire[count];
        for (int i = 0; i < count; i++)
        {
            ExecutionPayloadBodyV1Result? b = bodies[i];
            arr[i] = new() { Body = b is null ? [] : [b.ToBodyWire()] };
        }
        return EncodeToWriter(new PayloadBodiesV1ResponseWire { PayloadBodies = arr }, writer);
    }

    public static int EncodePayloadBodiesV2Response(IReadOnlyList<ExecutionPayloadBodyV2Result?> bodies, IBufferWriter<byte> writer)
    {
        int count = bodies.Count;
        NullablePayloadBodyV2Wire[] arr = new NullablePayloadBodyV2Wire[count];
        for (int i = 0; i < count; i++)
        {
            ExecutionPayloadBodyV2Result? b = bodies[i];
            arr[i] = new() { Body = b is null ? [] : [b.ToBodyWire()] };
        }
        return EncodeToWriter(new PayloadBodiesV2ResponseWire { PayloadBodies = arr }, writer);
    }

    public static int EncodeCapabilitiesResponse(IReadOnlyList<string> caps, IBufferWriter<byte> writer)
    {
        int count = caps.Count;
        SszCapabilityName[] arr = new SszCapabilityName[count];
        for (int i = 0; i < count; i++)
            arr[i] = new() { Name = Encoding.UTF8.GetBytes(caps[i]) };
        return EncodeToWriter(new ExchangeCapabilitiesResponseWire { Capabilities = arr }, writer);
    }

    public static string[] DecodeCapabilitiesRequest(ReadOnlySequence<byte> buf)
    {
        ExchangeCapabilitiesRequestWire.Decode(buf, out ExchangeCapabilitiesRequestWire wire);
        if (wire.Capabilities is null) return [];
        string[] result = new string[wire.Capabilities.Length];
        for (int i = 0; i < result.Length; i++)
            result[i] = Encoding.UTF8.GetString(wire.Capabilities[i].Name ?? []);
        return result;
    }

    public static ClientVersionV1 DecodeClientVersionRequest(ReadOnlySequence<byte> buf)
    {
        GetClientVersionRequestWire.Decode(buf, out GetClientVersionRequestWire wire);
        ClientVersionWire cl = wire.ClientVersion;
        return new ClientVersionV1
        {
            Code = Encoding.UTF8.GetString(cl.Code ?? []),
            Name = Encoding.UTF8.GetString(cl.Name ?? []),
            Version = Encoding.UTF8.GetString(cl.Version ?? []),
            Commit = cl.Commit is { Length: 4 } c ? Convert.ToHexString(c) : new string('0', 8),
        };
    }

    public static int EncodeClientVersionResponse(ClientVersionV1[] versions, IBufferWriter<byte> writer)
    {
        ClientVersionWire[] wireVersions = new ClientVersionWire[versions.Length];
        for (int i = 0; i < versions.Length; i++)
        {
            string commitHex = versions[i].Commit ?? string.Empty;
            byte[] commit = commitHex.Length >= 8
                ? Convert.FromHexString(commitHex[..8])
                : new byte[4];
            wireVersions[i] = new()
            {
                Code = Encoding.UTF8.GetBytes(versions[i].Code ?? string.Empty),
                Name = Encoding.UTF8.GetBytes(versions[i].Name ?? string.Empty),
                Version = Encoding.UTF8.GetBytes(versions[i].Version ?? string.Empty),
                Commit = commit
            };
        }
        return EncodeToWriter(new GetClientVersionResponseWire { Versions = wireVersions }, writer);
    }

    private static byte EngineStatusToSsz(string status) => status switch
    {
        PayloadStatus.Valid => 0,
        PayloadStatus.Invalid => 1,
        PayloadStatus.Syncing => 2,
        PayloadStatus.Accepted => 3,
        _ => throw new InvalidOperationException($"Unknown payload status '{status}': cannot map to SSZ wire byte")
    };

    private static PayloadStatusWire BuildPayloadStatusWire(PayloadStatusV1 ps)
    {
        const int MaxErrorBytes = 1024;
        byte[] errorBytes = ps.ValidationError is not null
            ? Encoding.UTF8.GetBytes(ps.ValidationError)
            : [];
        if (errorBytes.Length > MaxErrorBytes)
            errorBytes = errorBytes[..MaxErrorBytes];

        return new()
        {
            Status = EngineStatusToSsz(ps.Status),
            LatestValidHash = ps.LatestValidHash is not null ? [ps.LatestValidHash] : [],
            ValidationError = errorBytes
        };
    }

    internal static PayloadAttributes PayloadAttributesFromWire(PayloadAttributesV1Wire pa) =>
        BuildPayloadAttributes(pa.Timestamp, pa.PrevRandao, pa.SuggestedFeeRecipient);

    internal static PayloadAttributes PayloadAttributesFromWire(PayloadAttributesV2Wire pa) =>
        BuildPayloadAttributes(pa.Timestamp, pa.PrevRandao, pa.SuggestedFeeRecipient,
            withdrawals: pa.Withdrawals.ToDomain());

    internal static PayloadAttributes PayloadAttributesFromWire(PayloadAttributesV3Wire pa) =>
        BuildPayloadAttributes(pa.Timestamp, pa.PrevRandao, pa.SuggestedFeeRecipient,
            withdrawals: pa.Withdrawals.ToDomain(),
            parentBeaconBlockRoot: pa.ParentBeaconBlockRoot);

    internal static PayloadAttributes PayloadAttributesFromWire(PayloadAttributesWire pa) =>
        BuildPayloadAttributes(pa.Timestamp, pa.PrevRandao, pa.SuggestedFeeRecipient,
            withdrawals: pa.Withdrawals.ToDomain(),
            parentBeaconBlockRoot: pa.ParentBeaconBlockRoot,
            slotNumber: pa.SlotNumber);

    private static PayloadAttributes BuildPayloadAttributes(
        ulong timestamp,
        Hash256 prevRandao,
        Address suggestedFeeRecipient,
        Withdrawal[]? withdrawals = null,
        Hash256? parentBeaconBlockRoot = null,
        ulong? slotNumber = null) => new()
        {
            Timestamp = timestamp,
            PrevRandao = prevRandao,
            SuggestedFeeRecipient = suggestedFeeRecipient,
            Withdrawals = withdrawals,
            ParentBeaconBlockRoot = parentBeaconBlockRoot,
            SlotNumber = slotNumber
        };

}
