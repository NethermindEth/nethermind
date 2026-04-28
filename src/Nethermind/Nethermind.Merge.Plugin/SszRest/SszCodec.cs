// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Nethermind.Core;
using Nethermind.Core.Crypto;
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

    private static (byte[] buffer, int length) EncodePooled<T>(T value) where T : ISszCodec<T>
    {
        int length = T.GetLength(value);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(length);
        T.Encode(buffer.AsSpan(0, length), value);
        return (buffer, length);
    }

    private static T[] WrapBytes<T>(byte[][] src, Func<byte[], T> ctor)
    {
        if (src is null || src.Length == 0) return [];
        T[] result = new T[src.Length];
        for (int i = 0; i < src.Length; i++) result[i] = ctor(src[i]);
        return result;
    }

    public static (byte[] buffer, int length) EncodePayloadStatus(PayloadStatusV1 ps)
        => EncodePooled(BuildPayloadStatusWire(ps));

    public static (byte[] buffer, int length) EncodeForkchoiceUpdatedResponse(ForkchoiceUpdatedV1Result resp)
    {
        SszBytes8[]? pidList = null;
        if (resp.PayloadId is not null)
        {
            string hex = resp.PayloadId.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? resp.PayloadId[2..] : resp.PayloadId;

            byte[] padded = new byte[8];
            try
            {
                byte[] decoded = Convert.FromHexString(hex);
                decoded.AsSpan(0, Math.Min(decoded.Length, 8)).CopyTo(padded);
            }
            catch (FormatException) { }

            pidList = [new SszBytes8 { Bytes = padded }];
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
        (ForkchoiceStateWire fcState, PayloadAttributes? attrs) = version switch
        {
            <= 1 => DecodeForkchoice<ForkchoiceUpdatedV1RequestWire>(buf, w => w.ForkchoiceState, w => w.PayloadAttributes is { Length: > 0 } a ? PayloadAttributesFromWire(a[0]) : null),
            2 => DecodeForkchoice<ForkchoiceUpdatedV2RequestWire>(buf, w => w.ForkchoiceState, w => w.PayloadAttributes is { Length: > 0 } a ? PayloadAttributesFromWire(a[0]) : null),
            3 => DecodeForkchoice<ForkchoiceUpdatedV3RequestWire>(buf, w => w.ForkchoiceState, w => w.PayloadAttributes is { Length: > 0 } a ? PayloadAttributesFromWire(a[0]) : null),
            _ => DecodeForkchoice<ForkchoiceUpdatedRequestWire>(buf, w => w.ForkchoiceState, w => w.PayloadAttributes is { Length: > 0 } a ? PayloadAttributesFromWire(a[0]) : null),
        };

        ForkchoiceStateV1 state = new(
            headBlockHash: fcState.HeadBlockHash,
            finalizedBlockHash: fcState.FinalizedBlockHash,
            safeBlockHash: fcState.SafeBlockHash);

        return (state, attrs);
    }

    private static (ForkchoiceStateWire, PayloadAttributes?) DecodeForkchoice<TWire>(
        ReadOnlySpan<byte> buf,
        Func<TWire, ForkchoiceStateWire> getState,
        Func<TWire, PayloadAttributes?> getAttrs)
        where TWire : struct, ISszCodec<TWire>
    {
        TWire.Decode(buf, out TWire wire);
        return (getState(wire), getAttrs(wire));
    }

    public static (ExecutionPayload payload, byte[]?[] versionedHashes, Hash256? parentBeaconBlockRoot, byte[][]? executionRequests)
        DecodeNewPayloadRequest(ReadOnlySpan<byte> buf, int version)
    {
        if (version <= 1)
        {
            NewPayloadV1RequestWire.Decode(buf, out NewPayloadV1RequestWire w);
            return (ExecutionPayloadV1Ssz.Unwrap(w.ExecutionPayload), [], null, null);
        }
        if (version == 2)
        {
            NewPayloadV2RequestWire.Decode(buf, out NewPayloadV2RequestWire w);
            return (ExecutionPayloadSsz.Unwrap(w.ExecutionPayload), [], null, null);
        }
        if (version == 3)
        {
            NewPayloadV3RequestWire.Decode(buf, out NewPayloadV3RequestWire w);
            ExecutionPayloadV3 ep = ExecutionPayloadV3Ssz.Unwrap(w.ExecutionPayload);
            ep.ParentBeaconBlockRoot = w.ParentBeaconBlockRoot;
            return (ep, HashesFromWire(w.ExpectedBlobVersionedHashes), w.ParentBeaconBlockRoot, null);
        }
        if (version == 4)
        {
            NewPayloadV4RequestWire.Decode(buf, out NewPayloadV4RequestWire w);
            ExecutionPayloadV3 ep = ExecutionPayloadV3Ssz.Unwrap(w.ExecutionPayload);
            ep.ParentBeaconBlockRoot = w.ParentBeaconBlockRoot;
            return (ep,
                HashesFromWire(w.ExpectedBlobVersionedHashes),
                w.ParentBeaconBlockRoot,
                ExecutionRequestsFromWire(w.ExecutionRequests));
        }
        NewPayloadV5RequestWire.Decode(buf, out NewPayloadV5RequestWire w5);
        ExecutionPayloadV4 ep5 = ExecutionPayloadV4Ssz.Unwrap(w5.ExecutionPayload);
        ep5.ParentBeaconBlockRoot = w5.ParentBeaconBlockRoot;
        return (ep5,
            HashesFromWire(w5.ExpectedBlobVersionedHashes),
            w5.ParentBeaconBlockRoot,
            ExecutionRequestsFromWire(w5.ExecutionRequests));
    }

    public static (byte[] buffer, int length) EncodeGetPayloadV1Response(ExecutionPayload ep)
        => EncodePooled(ExecutionPayloadV1Ssz.Wrap(ep));

    public static (byte[] buffer, int length) EncodeGetPayloadV2Response(GetPayloadV2Result? r)
        => EncodePooled(new GetPayloadResponseV2Wire
        {
            ExecutionPayload = ExecutionPayloadSsz.Wrap(r!.ExecutionPayload),
            BlockValue = r.BlockValue
        });

    public static (byte[] buffer, int length) EncodeGetPayloadV3Response(GetPayloadV3Result? r)
        => EncodePooled(new GetPayloadResponseV3Wire
        {
            ExecutionPayload = ExecutionPayloadV3Ssz.Wrap(r!.ExecutionPayload),
            BlockValue = r.BlockValue,
            BlobsBundle = BlobsBundleToV1Wire(r.BlobsBundle),
            ShouldOverrideBuilder = r.ShouldOverrideBuilder
        });

    public static (byte[] buffer, int length) EncodeGetPayloadV4Response(GetPayloadV4Result? r)
        => EncodePooled(new GetPayloadResponseV4Wire
        {
            ExecutionPayload = ExecutionPayloadV3Ssz.Wrap(r!.ExecutionPayload),
            BlockValue = r.BlockValue,
            BlobsBundle = BlobsBundleToV1Wire(r.BlobsBundle),
            ShouldOverrideBuilder = r.ShouldOverrideBuilder,
            ExecutionRequests = ExecutionRequestsToWire(r.ExecutionRequests)
        });

    public static (byte[] buffer, int length) EncodeGetPayloadV5Response(GetPayloadV5Result? r)
        => EncodePooled(new GetPayloadResponseV5Wire
        {
            ExecutionPayload = ExecutionPayloadV3Ssz.Wrap(r!.ExecutionPayload),
            BlockValue = r.BlockValue,
            BlobsBundle = BlobsBundleToV2Wire(r.BlobsBundle),
            ShouldOverrideBuilder = r.ShouldOverrideBuilder,
            ExecutionRequests = ExecutionRequestsToWire(r.ExecutionRequests)
        });

    public static (byte[] buffer, int length) EncodeGetPayloadV6Response(GetPayloadV6Result? r)
        => EncodePooled(new GetPayloadResponseV6Wire
        {
            ExecutionPayload = ExecutionPayloadV4Ssz.Wrap(r!.ExecutionPayload),
            BlockValue = r.BlockValue,
            BlobsBundle = BlobsBundleToV2Wire(r.BlobsBundle),
            ShouldOverrideBuilder = r.ShouldOverrideBuilder,
            ExecutionRequests = ExecutionRequestsToWire(r.ExecutionRequests)
        });

    public static ReadOnlyMemory<byte>[] DecodeGetBlobsRequest(ReadOnlySpan<byte> buf)
    {
        GetBlobsRequestWire.Decode(buf, out GetBlobsRequestWire wire);
        if (wire.VersionedHashes is null) return [];
        ReadOnlyMemory<byte>[] result = new ReadOnlyMemory<byte>[wire.VersionedHashes.Length];
        for (int i = 0; i < result.Length; i++)
            result[i] = wire.VersionedHashes[i].Bytes.ToArray();
        return result;
    }

    public static (byte[] buffer, int length) EncodeGetBlobsV1Response(IReadOnlyList<BlobAndProofV1?> blobs)
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

    public static (byte[] buffer, int length) EncodeGetBlobsV2Response(IReadOnlyList<BlobAndProofV2?> blobs)
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

    public static (byte[] buffer, int length) EncodeGetBlobsV3Response(IReadOnlyList<BlobAndProofV2?> blobs)
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

    public static (byte[] buffer, int length) EncodePayloadBodiesV1Response(IReadOnlyList<ExecutionPayloadBodyV1Result?> bodies)
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
                    Transactions = TxsToWire((byte[][])b.Transactions),
                    Withdrawals = WithdrawalsToWire((Withdrawal[]?)b.Withdrawals)
                }]
            };
        }
        return EncodePooled(new PayloadBodiesV1ResponseWire { PayloadBodies = arr });
    }

    public static (byte[] buffer, int length) EncodePayloadBodiesV2Response(IReadOnlyList<ExecutionPayloadBodyV2Result?> bodies)
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
                    Transactions = TxsToWire((byte[][])b.Transactions),
                    Withdrawals = WithdrawalsToWire((Withdrawal[]?)b.Withdrawals),
                    BlockAccessList = b.BlockAccessList is not null
                        ? [new SszTransaction { Data = b.BlockAccessList }] : []
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

    public static (byte[] buffer, int length) EncodeTransitionConfigurationResponse(TransitionConfigurationV1 tc)
        => EncodePooled(new ExchangeTransitionConfigurationRequestWire
        {
            TransitionConfiguration = new()
            {
                TerminalTotalDifficulty = tc.TerminalTotalDifficulty ?? UInt256.Zero,
                TerminalBlockHash = tc.TerminalBlockHash ?? Hash256.Zero,
                TerminalBlockNumber = (ulong)tc.TerminalBlockNumber
            }
        });

    public static (byte[] buffer, int length) EncodeCapabilitiesResponse(IReadOnlyList<string> caps)
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

    public static (byte[] buffer, int length) EncodeClientVersionResponse(ClientVersionV1[] versions)
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

    private static PayloadAttributes PayloadAttributesFromWire(PayloadAttributesV1Wire pa) => new()
    {
        Timestamp = pa.Timestamp,
        PrevRandao = pa.PrevRandao,
        SuggestedFeeRecipient = pa.SuggestedFeeRecipient
    };

    private static PayloadAttributes PayloadAttributesFromWire(PayloadAttributesV2Wire pa) => new()
    {
        Timestamp = pa.Timestamp,
        PrevRandao = pa.PrevRandao,
        SuggestedFeeRecipient = pa.SuggestedFeeRecipient,
        Withdrawals = WithdrawalsFromWire(pa.Withdrawals)
    };

    private static PayloadAttributes PayloadAttributesFromWire(PayloadAttributesV3Wire pa) => new()
    {
        Timestamp = pa.Timestamp,
        PrevRandao = pa.PrevRandao,
        SuggestedFeeRecipient = pa.SuggestedFeeRecipient,
        Withdrawals = WithdrawalsFromWire(pa.Withdrawals),
        ParentBeaconBlockRoot = pa.ParentBeaconBlockRoot is { Length: 1 } ? pa.ParentBeaconBlockRoot[0] : null
    };

    private static PayloadAttributes PayloadAttributesFromWire(PayloadAttributesWire pa) => new()
    {
        Timestamp = pa.Timestamp,
        PrevRandao = pa.PrevRandao,
        SuggestedFeeRecipient = pa.SuggestedFeeRecipient,
        Withdrawals = WithdrawalsFromWire(pa.Withdrawals),
        ParentBeaconBlockRoot = pa.ParentBeaconBlockRoot is { Length: 1 } ? pa.ParentBeaconBlockRoot[0] : null,
        SlotNumber = pa.SlotNumber
    };

    private static WithdrawalWire[] WithdrawalsToWire(Withdrawal[]? ws)
    {
        if (ws is null || ws.Length == 0) return [];
        WithdrawalWire[] result = new WithdrawalWire[ws.Length];
        for (int i = 0; i < ws.Length; i++)
            result[i] = new WithdrawalWire
            {
                Index = ws[i].Index,
                ValidatorIndex = ws[i].ValidatorIndex,
                Address = ws[i].Address,
                Amount = ws[i].AmountInGwei
            };
        return result;
    }

    private static Withdrawal[] WithdrawalsFromWire(WithdrawalWire[]? ws)
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

    private static SszTransaction[] TxsToWire(byte[][] txs)
        => WrapBytes(txs, data => new SszTransaction { Data = data });

    private static byte[]?[] HashesFromWire(Hash256[]? hashes)
    {
        if (hashes is null || hashes.Length == 0) return [];
        byte[]?[] result = new byte[]?[hashes.Length];
        for (int i = 0; i < hashes.Length; i++)
        {
            byte[] bytes = new byte[32];
            ValueHash256 vh = hashes[i].ValueHash256;
            MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref vh, 1)).CopyTo(bytes);
            result[i] = bytes;
        }
        return result;
    }

    private static SszKzgCommitment[] KzgProofsToWire(byte[][] proofs)
        => WrapBytes(proofs, b => new SszKzgCommitment { Bytes = b });

    private static SszTransaction[] ExecutionRequestsToWire(byte[][]? reqs)
        => reqs is null ? [] : WrapBytes(reqs, data => new SszTransaction { Data = data });

    private static byte[][]? ExecutionRequestsFromWire(SszTransaction[]? reqs)
    {
        if (reqs is null || reqs.Length == 0) return null;
        byte[][] result = new byte[reqs.Length][];
        for (int i = 0; i < reqs.Length; i++) result[i] = reqs[i].Data ?? [];
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
        if (b is null) return new() { Commitments = [], Proofs = [], Blobs = [] };
        (SszKzgCommitment[] c, SszKzgCommitment[] p, SszBlob[] bl) = BuildBlobBundleArrays(b.Commitments, b.Proofs, b.Blobs);
        return new() { Commitments = c, Proofs = p, Blobs = bl };
    }

    private static BlobsBundleV2Wire BlobsBundleToV2Wire(BlobsBundleV2? b)
    {
        if (b is null) return new() { Commitments = [], Proofs = [], Blobs = [] };
        (SszKzgCommitment[] c, SszKzgCommitment[] p, SszBlob[] bl) = BuildBlobBundleArrays(b.Commitments, b.Proofs, b.Blobs);
        return new() { Commitments = c, Proofs = p, Blobs = bl };
    }
}
