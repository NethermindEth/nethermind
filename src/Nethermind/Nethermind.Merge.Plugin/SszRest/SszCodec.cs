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

    private static T[] WrapBytes<T>(byte[][] src, Func<byte[], T> ctor)
    {
        if (src is null || src.Length == 0) return [];
        T[] result = new T[src.Length];
        for (int i = 0; i < src.Length; i++) result[i] = ctor(src[i]);
        return result;
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

    public static (ForkchoiceStateV1 state, PayloadAttributes? attrs)
        DecodeForkchoiceUpdatedRequest(ReadOnlySpan<byte> buf, int version)
    {
        if (version <= 1)
        {
            ForkchoiceUpdatedV1RequestWire.Decode(buf, out ForkchoiceUpdatedV1RequestWire w1);
            return (ForkchoiceStateV1FromWire(w1.ForkchoiceState),
                w1.PayloadAttributes is { Length: > 0 } a1 ? PayloadAttributesFromWire(a1[0]) : null);
        }
        if (version == 2)
        {
            ForkchoiceUpdatedV2RequestWire.Decode(buf, out ForkchoiceUpdatedV2RequestWire w2);
            return (ForkchoiceStateV1FromWire(w2.ForkchoiceState),
                w2.PayloadAttributes is { Length: > 0 } a2 ? PayloadAttributesFromWire(a2[0]) : null);
        }
        if (version == 3)
        {
            ForkchoiceUpdatedV3RequestWire.Decode(buf, out ForkchoiceUpdatedV3RequestWire w3);
            return (ForkchoiceStateV1FromWire(w3.ForkchoiceState),
                w3.PayloadAttributes is { Length: > 0 } a3 ? PayloadAttributesFromWire(a3[0]) : null);
        }
        ForkchoiceUpdatedRequestWire.Decode(buf, out ForkchoiceUpdatedRequestWire wN);
        return (ForkchoiceStateV1FromWire(wN.ForkchoiceState),
            wN.PayloadAttributes is { Length: > 0 } aN ? PayloadAttributesFromWire(aN[0]) : null);
    }

    private static ForkchoiceStateV1 ForkchoiceStateV1FromWire(ForkchoiceStateWire w) => new(
        headBlockHash: w.HeadBlockHash,
        finalizedBlockHash: w.FinalizedBlockHash,
        safeBlockHash: w.SafeBlockHash);


    public static (ExecutionPayload payload, byte[]?[] versionedHashes, Hash256? parentBeaconBlockRoot, byte[][]? executionRequests)
        DecodeNewPayloadRequest(ReadOnlySpan<byte> buf, int version)
    {
        if (version <= 1)
        {
            NewPayloadV1RequestWire.Decode(buf, out NewPayloadV1RequestWire w);
            return (w.ExecutionPayload.Unwrap(), [], null, null);
        }
        if (version == 2)
        {
            NewPayloadV2RequestWire.Decode(buf, out NewPayloadV2RequestWire w);
            return (w.ExecutionPayload.Unwrap(), [], null, null);
        }
        if (version == 3)
        {
            NewPayloadV3RequestWire.Decode(buf, out NewPayloadV3RequestWire w);
            ExecutionPayloadV3 ep = w.ExecutionPayload.Unwrap();
            return (ep, HashesFromWire(w.ExpectedBlobVersionedHashes), w.ParentBeaconBlockRoot, null);
        }
        if (version == 4)
        {
            NewPayloadV4RequestWire.Decode(buf, out NewPayloadV4RequestWire w);
            ExecutionPayloadV3 ep = w.ExecutionPayload.Unwrap();
            return (ep,
                HashesFromWire(w.ExpectedBlobVersionedHashes),
                w.ParentBeaconBlockRoot,
                ExecutionRequestsFromWire(w.ExecutionRequests));
        }
        if (version == 5)
        {
            NewPayloadV5RequestWire.Decode(buf, out NewPayloadV5RequestWire w5);
            ExecutionPayloadV4 ep5 = w5.ExecutionPayload.Unwrap();
            return (ep5,
                HashesFromWire(w5.ExpectedBlobVersionedHashes),
                w5.ParentBeaconBlockRoot,
                ExecutionRequestsFromWire(w5.ExecutionRequests));
        }

        throw new NotSupportedException(
            $"Unsupported engine_newPayload SSZ wire version: {version}. " +
            $"Add a dedicated decode branch for this version before adding handler support.");
    }

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
            BlobsBundle = BlobsBundleToV1Wire(r.BlobsBundle),
            ShouldOverrideBuilder = r.ShouldOverrideBuilder
        });

    public static ArrayPoolSpan<byte> EncodeGetPayloadV4Response(GetPayloadV4Result? r)
        => EncodePooled(new GetPayloadResponseV4Wire
        {
            ExecutionPayload = new SszExecutionPayloadV3((ExecutionPayloadV3)r!.ExecutionPayload),
            BlockValue = r.BlockValue,
            BlobsBundle = BlobsBundleToV1Wire(r.BlobsBundle),
            ShouldOverrideBuilder = r.ShouldOverrideBuilder,
            ExecutionRequests = ExecutionRequestsToWire(r.ExecutionRequests)
        });

    public static ArrayPoolSpan<byte> EncodeGetPayloadV5Response(GetPayloadV5Result? r)
        => EncodePooled(new GetPayloadResponseV5Wire
        {
            ExecutionPayload = new SszExecutionPayloadV3((ExecutionPayloadV3)r!.ExecutionPayload),
            BlockValue = r.BlockValue,
            BlobsBundle = BlobsBundleToV2Wire(r.BlobsBundle),
            ShouldOverrideBuilder = r.ShouldOverrideBuilder,
            ExecutionRequests = ExecutionRequestsToWire(r.ExecutionRequests)
        });

    public static ArrayPoolSpan<byte> EncodeGetPayloadV6Response(GetPayloadV6Result? r)
        => EncodePooled(new GetPayloadResponseV6Wire
        {
            ExecutionPayload = new SszExecutionPayloadV4((ExecutionPayloadV4)r!.ExecutionPayload),
            BlockValue = r.BlockValue,
            BlobsBundle = BlobsBundleToV2Wire(r.BlobsBundle),
            ShouldOverrideBuilder = r.ShouldOverrideBuilder,
            ExecutionRequests = ExecutionRequestsToWire(r.ExecutionRequests)
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
        int count = blobs.Count;
        BlobAndProofV2Wire[] arr = new BlobAndProofV2Wire[count];
        int filled = 0;
        for (int i = 0; i < count; i++)
        {
            BlobAndProofV2? b = blobs[i];
            if (b is not null) arr[filled++] = new() { Blob = b.Blob, Proofs = KzgProofsToWire(b.Proofs) };
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
                : new() { BlobAndProof = [new() { Blob = b.Blob, Proofs = KzgProofsToWire(b.Proofs) }] };
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
            if (b is null) { arr[i] = new() { Body = [] }; continue; }
            arr[i] = new()
            {
                Body = [new()
                {
                    Transactions = TxsToWire(b.Transactions),
                    Withdrawals = WithdrawalsToWire(b.Withdrawals)
                }]
            };
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
            if (b is null) { arr[i] = new() { Body = [] }; continue; }
            arr[i] = new()
            {
                Body = [new()
                {
                    Transactions = TxsToWire(b.Transactions),
                    Withdrawals = WithdrawalsToWire(b.Withdrawals),
                    BlockAccessList = b.BlockAccessList is not null
                        ? [new SszTransaction { Bytes = b.BlockAccessList }] : []
                }]
            };
        }
        return EncodePooled(new PayloadBodiesV2ResponseWire { PayloadBodies = arr });
    }

    public static TransitionConfigurationV1 DecodeTransitionConfigurationRequest(ReadOnlySpan<byte> buf)
    {
        ExchangeTransitionConfigurationRequestWire.Decode(buf, out ExchangeTransitionConfigurationRequestWire wire);
        TransitionConfigurationV1Wire tc = wire.TransitionConfiguration;
        return new TransitionConfigurationV1
        {
            TerminalTotalDifficulty = tc.TerminalTotalDifficulty,
            TerminalBlockHash = tc.TerminalBlockHash,
            TerminalBlockNumber = (long)tc.TerminalBlockNumber
        };
    }

    public static ArrayPoolSpan<byte> EncodeTransitionConfigurationResponse(TransitionConfigurationV1 tc)
        => EncodePooled(new ExchangeTransitionConfigurationRequestWire
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
        "INVALID_BLOCK_HASH" => SszStatusInvalidBlockHash,
        _ => SszStatusInvalid
    };

    private static PayloadStatusWire BuildPayloadStatusWire(PayloadStatusV1 ps) => new()
    {
        Status = EngineStatusToSsz(ps.Status),
        LatestValidHash = ps.LatestValidHash is not null ? [ps.LatestValidHash] : [],
        ValidationError = ps.ValidationError is not null ? Encoding.UTF8.GetBytes(ps.ValidationError) : []
    };

    private static PayloadAttributes PayloadAttributesFromWire(PayloadAttributesV1Wire pa) =>
        BuildPayloadAttributes(pa.Timestamp, pa.PrevRandao, pa.SuggestedFeeRecipient);

    private static PayloadAttributes PayloadAttributesFromWire(PayloadAttributesV2Wire pa) =>
        BuildPayloadAttributes(pa.Timestamp, pa.PrevRandao, pa.SuggestedFeeRecipient,
            withdrawals: WithdrawalsFromWire(pa.Withdrawals));

    private static PayloadAttributes PayloadAttributesFromWire(PayloadAttributesV3Wire pa) =>
        BuildPayloadAttributes(pa.Timestamp, pa.PrevRandao, pa.SuggestedFeeRecipient,
            withdrawals: WithdrawalsFromWire(pa.Withdrawals),
            parentBeaconBlockRoot: pa.ParentBeaconBlockRoot is { Length: 1 } ? pa.ParentBeaconBlockRoot[0] : null);

    private static PayloadAttributes PayloadAttributesFromWire(PayloadAttributesWire pa) =>
        BuildPayloadAttributes(pa.Timestamp, pa.PrevRandao, pa.SuggestedFeeRecipient,
            withdrawals: WithdrawalsFromWire(pa.Withdrawals),
            parentBeaconBlockRoot: pa.ParentBeaconBlockRoot is { Length: 1 } ? pa.ParentBeaconBlockRoot[0] : null,
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

    private static SszTransaction[] TxsToWire(IReadOnlyList<byte[]> txs)
    {
        if (txs.Count == 0) return [];
        SszTransaction[] result = new SszTransaction[txs.Count];
        for (int i = 0; i < result.Length; i++) result[i] = new SszTransaction { Bytes = txs[i] };
        return result;
    }

    private static SszWithdrawal[] WithdrawalsToWire(IReadOnlyList<Withdrawal>? ws)
    {
        if (ws is null || ws.Count == 0) return [];
        SszWithdrawal[] result = new SszWithdrawal[ws.Count];
        for (int i = 0; i < ws.Count; i++)
            result[i] = new SszWithdrawal
            {
                Index = ws[i].Index,
                ValidatorIndex = ws[i].ValidatorIndex,
                Address = ws[i].Address,
                Amount = ws[i].AmountInGwei
            };
        return result;
    }

    private static Withdrawal[] WithdrawalsFromWire(SszWithdrawal[]? ws)
    {
        if (ws is null || ws.Length == 0) return [];
        Withdrawal[] result = new Withdrawal[ws.Length];
        for (int i = 0; i < ws.Length; i++)
            result[i] = new Withdrawal
            {
                Index = ws[i].Index,
                ValidatorIndex = ws[i].ValidatorIndex,
                Address = ws[i].Address,
                AmountInGwei = ws[i].Amount
            };
        return result;
    }

    private static byte[]?[] HashesFromWire(Hash256[]? hashes)
    {
        if (hashes is null || hashes.Length == 0) return [];
        byte[]?[] result = new byte[]?[hashes.Length];
        for (int i = 0; i < hashes.Length; i++)
        {
            byte[] bytes = new byte[32];
            hashes[i].Bytes.CopyTo(bytes);
            result[i] = bytes;
        }
        return result;
    }

    private static SszKzgCommitment[] KzgProofsToWire(byte[][] proofs)
        => WrapBytes(proofs, b => new SszKzgCommitment { Bytes = b });

    private static SszTransaction[] ExecutionRequestsToWire(byte[][]? reqs)
        => reqs is null ? [] : WrapBytes(reqs, data => new SszTransaction { Bytes = data });

    private static byte[][]? ExecutionRequestsFromWire(SszTransaction[]? reqs)
    {
        if (reqs is null || reqs.Length == 0) return null;
        byte[][] result = new byte[reqs.Length][];
        for (int i = 0; i < reqs.Length; i++) result[i] = reqs[i].Bytes ?? [];
        return result;
    }

    private static (SszKzgCommitment[] commitments, SszKzgCommitment[] proofs, SszBlob[] blobs)
        BuildBlobBundleArrays(byte[][] commitments, byte[][] proofs, byte[][] blobs)
    {
        SszKzgCommitment[] c = new SszKzgCommitment[commitments.Length];
        SszKzgCommitment[] p = new SszKzgCommitment[proofs.Length];
        SszBlob[] bl = new SszBlob[blobs.Length];
        for (int i = 0; i < c.Length; i++) c[i] = new() { Bytes = commitments[i] };
        for (int i = 0; i < p.Length; i++) p[i] = new() { Bytes = proofs[i] };
        for (int i = 0; i < bl.Length; i++) bl[i] = new() { Bytes = blobs[i] };
        return (c, p, bl);
    }

    private static BlobsBundleV1Wire BlobsBundleToV1Wire(BlobsBundleV1? b)
    {
        if (b?.Commitments is null) return new BlobsBundleV1Wire();
        (SszKzgCommitment[] c, SszKzgCommitment[] p, SszBlob[] bl) =
            BuildBlobBundleArrays(b.Commitments, b.Proofs!, b.Blobs!);
        return new BlobsBundleV1Wire { Commitments = c, Proofs = p, Blobs = bl };
    }

    private static BlobsBundleV2Wire BlobsBundleToV2Wire(BlobsBundleV2? b)
    {
        if (b?.Commitments is null) return new BlobsBundleV2Wire();
        (SszKzgCommitment[] c, SszKzgCommitment[] p, SszBlob[] bl) =
            BuildBlobBundleArrays(b.Commitments, b.Proofs!, b.Blobs!);
        return new BlobsBundleV2Wire { Commitments = c, Proofs = p, Blobs = bl };
    }
}
