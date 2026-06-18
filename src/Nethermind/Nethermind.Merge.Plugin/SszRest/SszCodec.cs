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
        SszPayloadId[]? pidList = null;
        if (resp.PayloadId is not null)
        {
            ReadOnlySpan<char> hex = resp.PayloadId.AsSpan();
            if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) hex = hex[2..];
            if (hex.Length != 16)
                throw new InvalidOperationException($"Invalid payload id '{resp.PayloadId}': expected 16 hex chars, got {hex.Length}");
            // ByteVector[8]: transmitted as-is (no LE flip — the bytes are already the
            // opaque token; the spec says treat payload_id as opaque bytes, not a uint64).
            byte[] idBytes = new byte[8];
            Bytes.FromHexString(hex, idBytes);
            pidList = [new SszPayloadId { Bytes = idBytes }];
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


    public static int EncodeBuiltPayloadParis(GetPayloadV2Result? r, IBufferWriter<byte> writer)
        => EncodeToWriter(new BuiltPayloadParisWire
        {
            ExecutionPayload = new SszExecutionPayloadV1(r!.ExecutionPayload),
            BlockValue = r.BlockValue
        }, writer);

    public static int EncodeGetPayloadV2Response(GetPayloadV2Result? r, IBufferWriter<byte> writer)
        => EncodeToWriter(new GetPayloadResponseV2Wire
        {
            ExecutionPayload = new SszExecutionPayloadV2(r!.ExecutionPayload),
            BlockValue = r.BlockValue
        }, writer);

    public static int EncodeGetPayloadV3Response(GetPayloadV3Result? r, IBufferWriter<byte> writer)
        => EncodeToWriter(new GetPayloadResponseV3Wire
        {
            ExecutionPayload = new SszExecutionPayloadV3(r!.ExecutionPayload),
            BlockValue = r.BlockValue,
            BlobsBundle = r.BlobsBundle.ToWire(),
            ShouldOverrideBuilder = r.ShouldOverrideBuilder
        }, writer);

    public static int EncodeGetPayloadV4Response(GetPayloadV4Result? r, IBufferWriter<byte> writer)
        => EncodeToWriter(new GetPayloadResponseV4Wire
        {
            ExecutionPayload = new SszExecutionPayloadV3(r!.ExecutionPayload),
            BlockValue = r.BlockValue,
            BlobsBundle = r.BlobsBundle.ToWire(),
            ExecutionRequests = r.ExecutionRequests.ToExecutionRequestsWire(),
            ShouldOverrideBuilder = r.ShouldOverrideBuilder
        }, writer);

    public static int EncodeGetPayloadV5Response(GetPayloadV5Result? r, IBufferWriter<byte> writer)
        => EncodeToWriter(new GetPayloadResponseV5Wire
        {
            ExecutionPayload = new SszExecutionPayloadV3(r!.ExecutionPayload),
            BlockValue = r.BlockValue,
            BlobsBundle = r.BlobsBundle.ToWire(),
            ExecutionRequests = r.ExecutionRequests.ToExecutionRequestsWire(),
            ShouldOverrideBuilder = r.ShouldOverrideBuilder
        }, writer);

    public static int EncodeGetPayloadV6Response(GetPayloadV6Result? r, IBufferWriter<byte> writer)
        => EncodeToWriter(new GetPayloadResponseV6Wire
        {
            ExecutionPayload = new SszExecutionPayloadV4(r!.ExecutionPayload),
            BlockValue = r.BlockValue,
            BlobsBundle = r.BlobsBundle.ToWire(),
            ExecutionRequests = r.ExecutionRequests.ToExecutionRequestsWire(),
            ShouldOverrideBuilder = r.ShouldOverrideBuilder
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
        int count = blobs.Count;
        BlobV1EntryWire[] arr = new BlobV1EntryWire[count];
        for (int i = 0; i < count; i++)
        {
            BlobAndProofV1? b = blobs[i];
            arr[i] = b is null
                ? new BlobV1EntryWire { Available = false, Contents = default }
                : new BlobV1EntryWire { Available = true, Contents = new() { Blob = b.Blob, Proof = b.Proof } };
        }
        return EncodeToWriter(new GetBlobsV1ResponseWire { Entries = arr }, writer);
    }

    public static int EncodeGetBlobsV2Response(IReadOnlyList<BlobAndProofV2?> blobs, IBufferWriter<byte> writer)
    {
        int count = blobs.Count;
        BlobV2EntryWire[] arr = new BlobV2EntryWire[count];
        for (int i = 0; i < count; i++)
        {
            BlobAndProofV2? b = blobs[i];
            arr[i] = b is null
                ? new BlobV2EntryWire { Available = false, Contents = default }
                : new BlobV2EntryWire { Available = true, Contents = new() { Blob = b.Blob, Proofs = b.Proofs.ToKzgWire() } };
        }
        return EncodeToWriter(new GetBlobsV2ResponseWire { Entries = arr }, writer);
    }

    // V3 entry shape is byte-identical to V2; only response-level semantics differ.
    public static int EncodeGetBlobsV3Response(IReadOnlyList<BlobAndProofV2?> blobs, IBufferWriter<byte> writer)
        => EncodeGetBlobsV2Response(blobs, writer);

    public static (byte[][] hashes, System.Collections.BitArray indices) DecodeGetBlobsV4Request(ReadOnlySequence<byte> buf)
    {
        GetBlobsV4RequestWire.Decode(buf, out GetBlobsV4RequestWire wire);
        if (wire.BlobVersionedHashes is null) return ([], new System.Collections.BitArray(128));
        byte[][] hashes = new byte[wire.BlobVersionedHashes.Length][];
        for (int i = 0; i < hashes.Length; i++)
            hashes[i] = wire.BlobVersionedHashes[i].Bytes.ToArray();
        return (hashes, wire.IndicesBitarray ?? new System.Collections.BitArray(128));
    }

    public static int EncodeGetBlobsV4Response(IReadOnlyList<BlobCellsAndProofs?> blobs, IBufferWriter<byte> writer)
    {
        const int CellsPerExtBlob = 128;
        int count = blobs.Count;
        BlobV4EntryWire[] arr = new BlobV4EntryWire[count];
        for (int i = 0; i < count; i++)
        {
            BlobCellsAndProofs? b = blobs[i];
            if (b is null || !b.Available)
            {
                arr[i] = new BlobV4EntryWire { Available = false, Contents = default };
                continue;
            }

            NullableBlobCellWire[] cells = new NullableBlobCellWire[CellsPerExtBlob];
            NullableKzgProofWire[] proofs = new NullableKzgProofWire[CellsPerExtBlob];
            byte[]?[]? srcCells = b.BlobCells;
            byte[]?[]? srcProofs = b.Proofs;
            for (int j = 0; j < CellsPerExtBlob; j++)
            {
                byte[]? cell = j < (srcCells?.Length ?? 0) ? srcCells![j] : null;
                byte[]? proof = j < (srcProofs?.Length ?? 0) ? srcProofs![j] : null;
                cells[j] = cell is null
                    ? new() { Cell = [] }
                    : new() { Cell = [SszBlobCell.FromSpan(cell.AsSpan(0, SszBlobCell.BlobCellLength))] };
                proofs[j] = proof is null
                    ? new() { Proof = [] }
                    : new() { Proof = [SszKzgCommitment.FromSpan(proof.AsSpan(0, SszKzgCommitment.KzgCommitmentLength))] };
            }

            arr[i] = new BlobV4EntryWire
            {
                Available = true,
                Contents = new BlobCellsAndProofsWire { BlobCells = cells, Proofs = proofs }
            };
        }
        return EncodeToWriter(new GetBlobsV4ResponseWire { Entries = arr }, writer);
    }

    public static Hash256[] DecodeGetPayloadBodiesByHashRequest(ReadOnlySequence<byte> buf)
    {
        GetPayloadBodiesByHashRequestWire.Decode(buf, out GetPayloadBodiesByHashRequestWire wire);
        return wire.BlockHashes ?? [];
    }

    public static (long start, long count) DecodeGetPayloadBodiesByRangeRequest(ReadOnlySequence<byte> buf)
    {
        GetPayloadBodiesByRangeRequestWire.Decode(buf, out GetPayloadBodiesByRangeRequestWire wire);
        return (SszNumericChecks.CheckedLong(wire.Start), SszNumericChecks.CheckedLong(wire.Count));
    }

    public static int EncodePayloadBodiesV1Response(IReadOnlyList<ExecutionPayloadBodyV1Result?> bodies, IBufferWriter<byte> writer)
    {
        int count = bodies.Count;
        BodyEntryV1Wire[] arr = new BodyEntryV1Wire[count];
        for (int i = 0; i < count; i++)
        {
            ExecutionPayloadBodyV1Result? b = bodies[i];
            arr[i] = b is null
                ? new BodyEntryV1Wire { Available = false, Body = default }
                : new BodyEntryV1Wire { Available = true, Body = b.ToBodyWire() };
        }
        return EncodeToWriter(new PayloadBodiesV1ResponseWire { Entries = arr }, writer);
    }

    public static int EncodePayloadBodiesV2Response(IReadOnlyList<ExecutionPayloadBodyV2Result?> bodies, IBufferWriter<byte> writer)
    {
        int count = bodies.Count;
        BodyEntryV2Wire[] arr = new BodyEntryV2Wire[count];
        for (int i = 0; i < count; i++)
        {
            ExecutionPayloadBodyV2Result? b = bodies[i];
            arr[i] = b is null
                ? new BodyEntryV2Wire { Available = false, Body = default }
                : new BodyEntryV2Wire { Available = true, Body = b.ToBodyWire() };
        }
        return EncodeToWriter(new PayloadBodiesV2ResponseWire { Entries = arr }, writer);
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
        SszValidationError[] error;
        if (ps.ValidationError is null)
        {
            error = [];
        }
        else
        {
            byte[] errorBytes = Encoding.UTF8.GetBytes(ps.ValidationError);
            if (errorBytes.Length > MaxErrorBytes) errorBytes = errorBytes[..MaxErrorBytes];
            error = [new SszValidationError { Bytes = errorBytes }];
        }

        return new()
        {
            Status = EngineStatusToSsz(ps.Status),
            LatestValidHash = ps.LatestValidHash is not null ? [ps.LatestValidHash] : [],
            ValidationError = error
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

    public static Hash256[] GetBlobVersionedHashes(ExecutionPayload payload)
    {
        Result<Transaction[]> decoded = payload.TryGetTransactions();
        if (decoded.IsError) return [];
        Transaction[] txs = decoded.Data;
        int totalHashes = 0;
        foreach (Transaction tx in txs)
            if (tx.BlobVersionedHashes is { } h) totalHashes += h.Length;
        List<Hash256> list = new(totalHashes);
        foreach (Transaction tx in txs)
        {
            byte[]?[]? hashes = tx.BlobVersionedHashes;
            if (hashes is null) continue;
            foreach (byte[]? h in hashes)
            {
                if (h is not null) list.Add(new Hash256(h));
            }
        }
        return list.ToArray();
    }
}
