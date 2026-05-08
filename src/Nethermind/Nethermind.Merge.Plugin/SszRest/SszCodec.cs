// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Consensus.Producers;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Serialization.Ssz;

namespace Nethermind.Merge.Plugin.SszRest;

/// <summary>
/// Converts between Engine API domain objects and SSZ wire types.
/// Encode/decode uses the SSZ source-generator (<see cref="ISszCodec{T}"/>)
/// </summary>
public static class SszCodec
{
    private const byte SszStatusValid = 0;
    private const byte SszStatusInvalid = 1;
    private const byte SszStatusSyncing = 2;
    private const byte SszStatusAccepted = 3;
    private const byte SszStatusInvalidBlockHash = 4;

    private static ArrayPoolSpan<byte> EncodePooled<T>(T value) where T : ISszCodec<T>
    {
        int length = T.GetLength(value);
        ArrayPoolSpan<byte> span = new(length);
        T.Encode(span, value);
        return span;
    }

    public static ArrayPoolSpan<byte> EncodePayloadStatus(PayloadStatusV1 ps)
        => EncodePooled(BuildPayloadStatusWire(ps));

    public static ArrayPoolSpan<byte> EncodeForkchoiceUpdatedResponse(ForkchoiceUpdatedV1Result resp)
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

        return EncodePooled(new ForkchoiceUpdatedResponseWire
        {
            PayloadStatus = BuildPayloadStatusWire(resp.PayloadStatus),
            PayloadId = pidList ?? []
        });
    }

    internal static ForkchoiceStateV1 ForkchoiceStateV1FromWire(ForkchoiceStateWire w) => new(
        headBlockHash: w.HeadBlockHash,
        finalizedBlockHash: w.FinalizedBlockHash,
        safeBlockHash: w.SafeBlockHash);


    public static ArrayPoolSpan<byte> EncodeGetPayloadV1Response(ExecutionPayload ep)
        => EncodePooled(new SszExecutionPayloadV1(ep));

    public static ArrayPoolSpan<byte> EncodeGetPayloadV2Response(GetPayloadV2Result? r)
        => EncodePooled(new GetPayloadResponseV2Wire
        {
            ExecutionPayload = new SszExecutionPayload(r!.ExecutionPayload),
            BlockValue = r.BlockValue
        });

    public static ArrayPoolSpan<byte> EncodeGetPayloadV3Response(GetPayloadV3Result? r)
        => EncodePooled(new GetPayloadResponseV3Wire
        {
            ExecutionPayload = new SszExecutionPayloadV3((ExecutionPayloadV3)r!.ExecutionPayload),
            BlockValue = r.BlockValue,
            BlobsBundle = r.BlobsBundle.ToWire(),
            ShouldOverrideBuilder = r.ShouldOverrideBuilder
        });

    public static ArrayPoolSpan<byte> EncodeGetPayloadV4Response(GetPayloadV4Result? r)
        => EncodePooled(new GetPayloadResponseV4Wire
        {
            ExecutionPayload = new SszExecutionPayloadV3((ExecutionPayloadV3)r!.ExecutionPayload),
            BlockValue = r.BlockValue,
            BlobsBundle = r.BlobsBundle.ToWire(),
            ShouldOverrideBuilder = r.ShouldOverrideBuilder,
            ExecutionRequests = r.ExecutionRequests.ToExecutionRequestsWire()
        });

    public static ArrayPoolSpan<byte> EncodeGetPayloadV5Response(GetPayloadV5Result? r)
        => EncodePooled(new GetPayloadResponseV5Wire
        {
            ExecutionPayload = new SszExecutionPayloadV3((ExecutionPayloadV3)r!.ExecutionPayload),
            BlockValue = r.BlockValue,
            BlobsBundle = r.BlobsBundle.ToWire(),
            ShouldOverrideBuilder = r.ShouldOverrideBuilder,
            ExecutionRequests = r.ExecutionRequests.ToExecutionRequestsWire()
        });

    public static ArrayPoolSpan<byte> EncodeGetPayloadV6Response(GetPayloadV6Result? r)
        => EncodePooled(new GetPayloadResponseV6Wire
        {
            ExecutionPayload = new SszExecutionPayloadV4((ExecutionPayloadV4)r!.ExecutionPayload),
            BlockValue = r.BlockValue,
            BlobsBundle = r.BlobsBundle.ToWire(),
            ShouldOverrideBuilder = r.ShouldOverrideBuilder,
            ExecutionRequests = r.ExecutionRequests.ToExecutionRequestsWire()
        });

    public static byte[][] DecodeGetBlobsRequest(ReadOnlySpan<byte> buf)
    {
        GetBlobsRequestWire.Decode(buf, out GetBlobsRequestWire wire);
        if (wire.VersionedHashes is null) return [];
        byte[][] result = new byte[wire.VersionedHashes.Length][];
        for (int i = 0; i < result.Length; i++)
            result[i] = wire.VersionedHashes[i].Bytes.ToArray();
        return result;
    }

    public static ArrayPoolSpan<byte> EncodeGetBlobsV1Response(IReadOnlyList<BlobAndProofV1?> blobs)
    {
        // V1 SSZ has no nullable wrapper, nulls (unknown hashes) are dropped and the CL
        // infers misses by comparing response length to request length.
        int count = blobs.Count;
        BlobAndProofV1Wire[] arr = new BlobAndProofV1Wire[count];
        int filled = 0;
        for (int i = 0; i < count; i++)
        {
            BlobAndProofV1? b = blobs[i];
            if (b is not null) arr[filled++] = new() { Blob = b.Blob, Proof = b.Proof };
        }
        return EncodePooled(new GetBlobsV1ResponseWire { BlobsAndProofs = arr[..filled] });
    }

    public static ArrayPoolSpan<byte> EncodeGetBlobsV2Response(IReadOnlyList<BlobAndProofV2?> blobs)
    {
        // Same null-drop as V1.
        int count = blobs.Count;
        BlobAndProofV2Wire[] arr = new BlobAndProofV2Wire[count];
        int filled = 0;
        for (int i = 0; i < count; i++)
        {
            BlobAndProofV2? b = blobs[i];
            if (b is not null) arr[filled++] = new() { Blob = b.Blob, Proofs = b.Proofs.ToKzgWire() };
        }
        return EncodePooled(new GetBlobsV2ResponseWire { BlobsAndProofs = arr[..filled] });
    }

    public static ArrayPoolSpan<byte> EncodeGetBlobsV3Response(IReadOnlyList<BlobAndProofV2?> blobs)
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
        return EncodePooled(new GetBlobsV3ResponseWire { BlobsAndProofs = arr });
    }

    public static Hash256[] DecodeGetPayloadBodiesByHashRequest(ReadOnlySpan<byte> buf)
    {
        GetPayloadBodiesByHashRequestWire.Decode(buf, out GetPayloadBodiesByHashRequestWire wire);
        return wire.BlockHashes ?? [];
    }

    public static (long start, long count) DecodeGetPayloadBodiesByRangeRequest(ReadOnlySpan<byte> buf)
    {
        GetPayloadBodiesByRangeRequestWire.Decode(buf, out GetPayloadBodiesByRangeRequestWire wire);
        return ((long)wire.Start, (long)wire.Count);
    }

    public static ArrayPoolSpan<byte> EncodePayloadBodiesV1Response(IReadOnlyList<ExecutionPayloadBodyV1Result?> bodies)
    {
        int count = bodies.Count;
        NullablePayloadBodyV1Wire[] arr = new NullablePayloadBodyV1Wire[count];
        for (int i = 0; i < count; i++)
        {
            ExecutionPayloadBodyV1Result? b = bodies[i];
            arr[i] = new() { Body = b is null ? [] : [b.ToBodyWire()] };
        }
        return EncodePooled(new PayloadBodiesV1ResponseWire { PayloadBodies = arr });
    }

    public static ArrayPoolSpan<byte> EncodePayloadBodiesV2Response(IReadOnlyList<ExecutionPayloadBodyV2Result?> bodies)
    {
        int count = bodies.Count;
        NullablePayloadBodyV2Wire[] arr = new NullablePayloadBodyV2Wire[count];
        for (int i = 0; i < count; i++)
        {
            ExecutionPayloadBodyV2Result? b = bodies[i];
            arr[i] = new() { Body = b is null ? [] : [b.ToBodyWire()] };
        }
        return EncodePooled(new PayloadBodiesV2ResponseWire { PayloadBodies = arr });
    }

    public static TransitionConfigurationV1 DecodeTransitionConfigurationRequest(ReadOnlySpan<byte> buf)
    {
        ExchangeTransitionConfigurationWire.Decode(buf, out ExchangeTransitionConfigurationWire wire);
        TransitionConfigurationV1Wire tc = wire.TransitionConfiguration;
        return new TransitionConfigurationV1
        {
            TerminalTotalDifficulty = tc.TerminalTotalDifficulty,
            TerminalBlockHash = tc.TerminalBlockHash,
            TerminalBlockNumber = (long)tc.TerminalBlockNumber
        };
    }

    public static ArrayPoolSpan<byte> EncodeTransitionConfigurationResponse(TransitionConfigurationV1 tc)
        => EncodePooled(new ExchangeTransitionConfigurationWire
        {
            TransitionConfiguration = new()
            {
                TerminalTotalDifficulty = tc.TerminalTotalDifficulty ?? UInt256.Zero,
                TerminalBlockHash = tc.TerminalBlockHash ?? Hash256.Zero,
                TerminalBlockNumber = (ulong)tc.TerminalBlockNumber
            }
        });

    public static ArrayPoolSpan<byte> EncodeCapabilitiesResponse(IReadOnlyList<string> caps)
    {
        int count = caps.Count;
        SszCapabilityName[] arr = new SszCapabilityName[count];
        for (int i = 0; i < count; i++)
            arr[i] = new() { Name = Encoding.UTF8.GetBytes(caps[i]) };
        return EncodePooled(new ExchangeCapabilitiesResponseWire { Capabilities = arr });
    }

    public static string[] DecodeCapabilitiesRequest(ReadOnlySpan<byte> buf)
    {
        ExchangeCapabilitiesRequestWire.Decode(buf, out ExchangeCapabilitiesRequestWire wire);
        if (wire.Capabilities is null) return [];
        string[] result = new string[wire.Capabilities.Length];
        for (int i = 0; i < result.Length; i++)
            result[i] = Encoding.UTF8.GetString(wire.Capabilities[i].Name ?? []);
        return result;
    }

    public static ClientVersionV1 DecodeClientVersionRequest(ReadOnlySpan<byte> buf)
    {
        GetClientVersionRequestWire.Decode(buf, out GetClientVersionRequestWire _);
        return new ClientVersionV1();
    }

    public static ArrayPoolSpan<byte> EncodeClientVersionResponse(ClientVersionV1[] versions)
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
        return EncodePooled(new GetClientVersionResponseWire { Versions = wireVersions });
    }

    private static byte EngineStatusToSsz(string status) => status switch
    {
        PayloadStatus.Valid => SszStatusValid,
        PayloadStatus.Invalid => SszStatusInvalid,
        PayloadStatus.Syncing => SszStatusSyncing,
        PayloadStatus.Accepted => SszStatusAccepted,
        PayloadStatus.InvalidBlockHash => SszStatusInvalidBlockHash,
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
