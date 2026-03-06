// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Consensus.Producers;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Serialization;

namespace Nethermind.Merge.Plugin.SszRest;

/// <summary>
/// SSZ codec for EIP-8161 SSZ-REST Engine API transport.
/// Uses the Nethermind SSZ source generator for ALL wire types.
/// </summary>
public static class SszRestCodec
{
    private const byte SszStatusValid = 0;
    private const byte SszStatusInvalid = 1;
    private const byte SszStatusSyncing = 2;
    private const byte SszStatusAccepted = 3;
    private const byte SszStatusInvalidBlockHash = 4;

    #region PayloadStatus

    public static byte[] EncodePayloadStatus(PayloadStatusV1 ps)
    {
        PayloadStatusWire wire = new()
        {
            Status = [EngineStatusToSsz(ps.Status)],
            LatestValidHash = ps.LatestValidHash is not null
                ? [new SszHash32 { Bytes = ps.LatestValidHash.Bytes.ToArray() }]
                : [],
            ValidationError = ps.ValidationError is not null
                ? Encoding.UTF8.GetBytes(ps.ValidationError)
                : []
        };

        return SszEncoding.Encode(wire);
    }

    public static PayloadStatusV1 DecodePayloadStatus(ReadOnlySpan<byte> buf)
    {
        SszEncoding.Decode(buf, out PayloadStatusWire wire);

        PayloadStatusV1 ps = new() { Status = SszToEngineStatus(wire.Status[0]) };

        if (wire.LatestValidHash is { Length: > 0 })
            ps.LatestValidHash = new Hash256(wire.LatestValidHash[0].Bytes);

        if (wire.ValidationError is { Length: > 0 })
            ps.ValidationError = Encoding.UTF8.GetString(wire.ValidationError);

        return ps;
    }

    #endregion

    #region ForkchoiceUpdated Response

    public static byte[] EncodeForkchoiceUpdatedResponse(ForkchoiceUpdatedV1Result resp)
    {
        PayloadStatusWire psWire = new()
        {
            Status = [EngineStatusToSsz(resp.PayloadStatus.Status)],
            LatestValidHash = resp.PayloadStatus.LatestValidHash is not null
                ? [new SszHash32 { Bytes = resp.PayloadStatus.LatestValidHash.Bytes.ToArray() }]
                : [],
            ValidationError = resp.PayloadStatus.ValidationError is not null
                ? Encoding.UTF8.GetBytes(resp.PayloadStatus.ValidationError)
                : []
        };

        SszPayloadId[] pidList;
        if (resp.PayloadId is not null)
        {
            byte[] payloadIdBytes = Convert.FromHexString(
                resp.PayloadId.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    ? resp.PayloadId[2..]
                    : resp.PayloadId);
            byte[] pidBuf = new byte[8];
            payloadIdBytes.AsSpan(0, Math.Min(8, payloadIdBytes.Length)).CopyTo(pidBuf);
            pidList = [new SszPayloadId { Bytes = pidBuf }];
        }
        else
        {
            pidList = [];
        }

        ForkchoiceUpdatedResponseWire wire = new()
        {
            PayloadStatus = psWire,
            PayloadId = pidList
        };

        return SszEncoding.Encode(wire);
    }

    #endregion

    #region ForkchoiceUpdated Request

    public static (ForkchoiceStateV1 state, PayloadAttributes? attributes) DecodeForkchoiceUpdatedRequest(ReadOnlySpan<byte> buf, int version)
    {
        SszEncoding.Decode(buf, out ForkchoiceUpdatedRequestWire wire);

        ForkchoiceStateV1 state = new(
            headBlockHash: new Hash256(wire.ForkchoiceState.HeadBlockHash),
            finalizedBlockHash: new Hash256(wire.ForkchoiceState.FinalizedBlockHash),
            safeBlockHash: new Hash256(wire.ForkchoiceState.SafeBlockHash)
        );

        PayloadAttributes? attributes = null;
        if (wire.PayloadAttributes is { Length: > 0 })
        {
            PayloadAttributesV3Wire pa = wire.PayloadAttributes[0];
            attributes = new PayloadAttributes
            {
                Timestamp = pa.Timestamp,
                PrevRandao = new Hash256(pa.PrevRandao),
                SuggestedFeeRecipient = new Address(pa.SuggestedFeeRecipient),
                ParentBeaconBlockRoot = new Hash256(pa.ParentBeaconBlockRoot)
            };

            if (pa.Withdrawals is { Length: > 0 })
            {
                attributes.Withdrawals = new Withdrawal[pa.Withdrawals.Length];
                for (int i = 0; i < pa.Withdrawals.Length; i++)
                {
                    attributes.Withdrawals[i] = WithdrawalFromWire(pa.Withdrawals[i]);
                }
            }
            else
            {
                attributes.Withdrawals = [];
            }
        }

        return (state, attributes);
    }

    #endregion

    #region NewPayload Request

    public static (ExecutionPayloadV3 payload, byte[]?[] versionedHashes, Hash256? parentBeaconBlockRoot, byte[][]? executionRequests) DecodeNewPayloadRequest(ReadOnlySpan<byte> buf, int version)
    {
        if (version <= 2)
        {
            ExecutionPayloadV3 ep = DecodeExecutionPayloadDirect(buf, version);
            return (ep, [], null, null);
        }

        if (version == 3)
        {
            SszEncoding.Decode(buf, out NewPayloadV3RequestWire wire);
            ExecutionPayloadV3 ep = ExecutionPayloadFromV3Wire(wire.ExecutionPayload);
            byte[]?[] hashes = HashArrayFromSszHash32(wire.VersionedHashes);
            Hash256 root = new(wire.ParentBeaconBlockRoot);
            return (ep, hashes, root, null);
        }

        // V4+
        SszEncoding.Decode(buf, out NewPayloadV4RequestWire wire4);
        ExecutionPayloadV3 ep4 = ExecutionPayloadFromV3Wire(wire4.ExecutionPayload);
        byte[]?[] hashes4 = HashArrayFromSszHash32(wire4.VersionedHashes);
        Hash256 root4 = new(wire4.ParentBeaconBlockRoot);
        byte[][] reqs = ExecutionRequestsFromWire(wire4.ExecutionRequests);
        return (ep4, hashes4, root4, reqs);
    }

    #endregion

    #region ExecutionPayload Encode

    public static byte[] EncodeExecutionPayload(ExecutionPayload ep, int version)
    {
        if (version >= 3)
        {
            ExecutionPayloadV3Wire wire = ExecutionPayloadToV3Wire(ep);
            return SszEncoding.Encode(wire);
        }

        if (version == 2)
        {
            ExecutionPayloadV2Wire wire = ExecutionPayloadToV2Wire(ep);
            return SszEncoding.Encode(wire);
        }

        ExecutionPayloadV1Wire wire1 = ExecutionPayloadToV1Wire(ep);
        return SszEncoding.Encode(wire1);
    }

    #endregion

    #region GetPayload Response

    public static byte[] EncodeGetPayloadResponse(ExecutionPayload ep, UInt256 blockValue, BlobsBundleV1? blobsBundle, bool shouldOverrideBuilder, byte[][]? executionRequests, int version)
    {
        if (version == 1)
            return EncodeExecutionPayload(ep, 1);

        GetPayloadResponseWire wire = new()
        {
            ExecutionPayload = ExecutionPayloadToV3Wire(ep),
            BlockValue = blockValue,
            BlobsBundle = BlobsBundleToWire(blobsBundle),
            ShouldOverrideBuilder = shouldOverrideBuilder,
            ExecutionRequests = ExecutionRequestsToWire(executionRequests)
        };

        return SszEncoding.Encode(wire);
    }

    #endregion

    #region Capabilities

    public static byte[] EncodeCapabilities(IEnumerable<string> capabilities)
    {
        List<SszCapability> caps = new();
        foreach (string cap in capabilities)
            caps.Add(new SszCapability { Name = Encoding.UTF8.GetBytes(cap) });

        ExchangeCapabilitiesWire wire = new() { Capabilities = caps.ToArray() };
        return SszEncoding.Encode(wire);
    }

    public static string[] DecodeCapabilities(ReadOnlySpan<byte> buf)
    {
        SszEncoding.Decode(buf, out ExchangeCapabilitiesWire wire);
        string[] result = new string[wire.Capabilities.Length];
        for (int i = 0; i < wire.Capabilities.Length; i++)
            result[i] = Encoding.UTF8.GetString(wire.Capabilities[i].Name);
        return result;
    }

    #endregion

    #region ClientVersion

    public static byte[] EncodeClientVersions(ClientVersionV1[] versions)
    {
        ClientVersionV1Wire[] wireVersions = new ClientVersionV1Wire[versions.Length];
        for (int i = 0; i < versions.Length; i++)
        {
            string commitHex = versions[i].Commit;
            byte[] commitBytes = new byte[4];
            if (commitHex.Length >= 8)
                commitBytes = Convert.FromHexString(commitHex[..8]);

            wireVersions[i] = new ClientVersionV1Wire
            {
                Code = Encoding.UTF8.GetBytes(versions[i].Code),
                Name = Encoding.UTF8.GetBytes(versions[i].Name),
                Version = Encoding.UTF8.GetBytes(versions[i].Version),
                Commit = commitBytes
            };
        }

        ClientVersionResponseWire wire = new() { Versions = wireVersions };
        return SszEncoding.Encode(wire);
    }

    public static ClientVersionV1 DecodeClientVersions(ReadOnlySpan<byte> buf)
    {
        SszEncoding.Decode(buf, out ClientVersionV1Wire wire);
        // We only use the incoming client version for validation/logging, return default
        return new ClientVersionV1();
    }

    #endregion

    #region GetBlobs Request

    public static byte[][] DecodeGetBlobsRequest(ReadOnlySpan<byte> buf)
    {
        SszEncoding.Decode(buf, out GetBlobsRequestWire wire);
        byte[][] hashes = new byte[wire.VersionedHashes.Length][];
        for (int i = 0; i < wire.VersionedHashes.Length; i++)
            hashes[i] = wire.VersionedHashes[i].Bytes;
        return hashes;
    }

    #endregion

    #region GetPayload Request

    public static byte[] DecodeGetPayloadRequest(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < 8)
            throw new SszDecodingException($"GetPayloadRequest: buffer too short ({buf.Length} < 8)");
        return buf[..8].ToArray();
    }

    #endregion

    #region Wire ↔ Domain conversion helpers

    private static byte EngineStatusToSsz(string status) => status switch
    {
        PayloadStatus.Valid => SszStatusValid,
        PayloadStatus.Invalid => SszStatusInvalid,
        PayloadStatus.Syncing => SszStatusSyncing,
        PayloadStatus.Accepted => SszStatusAccepted,
        "INVALID_BLOCK_HASH" => SszStatusInvalidBlockHash,
        _ => SszStatusInvalid
    };

    private static string SszToEngineStatus(byte status) => status switch
    {
        SszStatusValid => PayloadStatus.Valid,
        SszStatusInvalid => PayloadStatus.Invalid,
        SszStatusSyncing => PayloadStatus.Syncing,
        SszStatusAccepted => PayloadStatus.Accepted,
        SszStatusInvalidBlockHash => "INVALID_BLOCK_HASH",
        _ => PayloadStatus.Invalid
    };

    private static Withdrawal WithdrawalFromWire(WithdrawalWire wd) => new()
    {
        Index = wd.Index,
        ValidatorIndex = wd.ValidatorIndex,
        Address = new Address(wd.Address),
        AmountInGwei = wd.Amount
    };

    private static WithdrawalWire WithdrawalToWire(Withdrawal wd) => new()
    {
        Index = wd.Index,
        ValidatorIndex = wd.ValidatorIndex,
        Address = wd.Address.Bytes,
        Amount = wd.AmountInGwei
    };

    private static SszTransaction[] TransactionsToWire(byte[][] txs)
    {
        if (txs is null || txs.Length == 0) return [];
        SszTransaction[] result = new SszTransaction[txs.Length];
        for (int i = 0; i < txs.Length; i++)
            result[i] = new SszTransaction { Data = txs[i] };
        return result;
    }

    private static byte[][] TransactionsFromWire(SszTransaction[] txs)
    {
        if (txs is null || txs.Length == 0) return [];
        byte[][] result = new byte[txs.Length][];
        for (int i = 0; i < txs.Length; i++)
            result[i] = txs[i].Data;
        return result;
    }

    private static byte[]?[] HashArrayFromSszHash32(SszHash32[] hashes)
    {
        if (hashes is null || hashes.Length == 0) return [];
        byte[]?[] result = new byte[hashes.Length][];
        for (int i = 0; i < hashes.Length; i++)
            result[i] = hashes[i].Bytes;
        return result;
    }

    private static ExecutionPayloadV1Wire ExecutionPayloadToV1Wire(ExecutionPayload ep) => new()
    {
        ParentHash = ep.ParentHash.Bytes.ToArray(),
        FeeRecipient = ep.FeeRecipient.Bytes,
        StateRoot = ep.StateRoot.Bytes.ToArray(),
        ReceiptsRoot = ep.ReceiptsRoot.Bytes.ToArray(),
        LogsBloom = ep.LogsBloom.Bytes.ToArray(),
        PrevRandao = ep.PrevRandao.Bytes.ToArray(),
        BlockNumber = (ulong)ep.BlockNumber,
        GasLimit = (ulong)ep.GasLimit,
        GasUsed = (ulong)ep.GasUsed,
        Timestamp = ep.Timestamp,
        ExtraData = ep.ExtraData ?? [],
        BaseFeePerGas = ep.BaseFeePerGas,
        BlockHash = ep.BlockHash.Bytes.ToArray(),
        Transactions = TransactionsToWire(ep.Transactions)
    };

    private static ExecutionPayloadV2Wire ExecutionPayloadToV2Wire(ExecutionPayload ep) => new()
    {
        ParentHash = ep.ParentHash.Bytes.ToArray(),
        FeeRecipient = ep.FeeRecipient.Bytes,
        StateRoot = ep.StateRoot.Bytes.ToArray(),
        ReceiptsRoot = ep.ReceiptsRoot.Bytes.ToArray(),
        LogsBloom = ep.LogsBloom.Bytes.ToArray(),
        PrevRandao = ep.PrevRandao.Bytes.ToArray(),
        BlockNumber = (ulong)ep.BlockNumber,
        GasLimit = (ulong)ep.GasLimit,
        GasUsed = (ulong)ep.GasUsed,
        Timestamp = ep.Timestamp,
        ExtraData = ep.ExtraData ?? [],
        BaseFeePerGas = ep.BaseFeePerGas,
        BlockHash = ep.BlockHash.Bytes.ToArray(),
        Transactions = TransactionsToWire(ep.Transactions),
        Withdrawals = WithdrawalsToWireArray(ep.Withdrawals)
    };

    private static ExecutionPayloadV3Wire ExecutionPayloadToV3Wire(ExecutionPayload ep) => new()
    {
        ParentHash = ep.ParentHash.Bytes.ToArray(),
        FeeRecipient = ep.FeeRecipient.Bytes,
        StateRoot = ep.StateRoot.Bytes.ToArray(),
        ReceiptsRoot = ep.ReceiptsRoot.Bytes.ToArray(),
        LogsBloom = ep.LogsBloom.Bytes.ToArray(),
        PrevRandao = ep.PrevRandao.Bytes.ToArray(),
        BlockNumber = (ulong)ep.BlockNumber,
        GasLimit = (ulong)ep.GasLimit,
        GasUsed = (ulong)ep.GasUsed,
        Timestamp = ep.Timestamp,
        ExtraData = ep.ExtraData ?? [],
        BaseFeePerGas = ep.BaseFeePerGas,
        BlockHash = ep.BlockHash.Bytes.ToArray(),
        Transactions = TransactionsToWire(ep.Transactions),
        Withdrawals = WithdrawalsToWireArray(ep.Withdrawals),
        BlobGasUsed = ep.BlobGasUsed ?? 0,
        ExcessBlobGas = ep.ExcessBlobGas ?? 0
    };

    private static ExecutionPayloadV3 ExecutionPayloadFromV3Wire(ExecutionPayloadV3Wire wire)
    {
        ExecutionPayloadV3 ep = new()
        {
            ParentHash = new Hash256(wire.ParentHash),
            FeeRecipient = new Address(wire.FeeRecipient),
            StateRoot = new Hash256(wire.StateRoot),
            ReceiptsRoot = new Hash256(wire.ReceiptsRoot),
            LogsBloom = new Bloom(wire.LogsBloom),
            PrevRandao = new Hash256(wire.PrevRandao),
            BlockNumber = (long)wire.BlockNumber,
            GasLimit = (long)wire.GasLimit,
            GasUsed = (long)wire.GasUsed,
            Timestamp = wire.Timestamp,
            ExtraData = wire.ExtraData,
            BaseFeePerGas = wire.BaseFeePerGas,
            BlockHash = new Hash256(wire.BlockHash),
            Transactions = TransactionsFromWire(wire.Transactions),
            Withdrawals = WithdrawalsFromWireArray(wire.Withdrawals),
            BlobGasUsed = wire.BlobGasUsed,
            ExcessBlobGas = wire.ExcessBlobGas
        };
        return ep;
    }

    private static ExecutionPayloadV3 DecodeExecutionPayloadDirect(ReadOnlySpan<byte> buf, int version)
    {
        if (version == 1)
        {
            SszEncoding.Decode(buf, out ExecutionPayloadV1Wire wire1);
            return new ExecutionPayloadV3
            {
                ParentHash = new Hash256(wire1.ParentHash),
                FeeRecipient = new Address(wire1.FeeRecipient),
                StateRoot = new Hash256(wire1.StateRoot),
                ReceiptsRoot = new Hash256(wire1.ReceiptsRoot),
                LogsBloom = new Bloom(wire1.LogsBloom),
                PrevRandao = new Hash256(wire1.PrevRandao),
                BlockNumber = (long)wire1.BlockNumber,
                GasLimit = (long)wire1.GasLimit,
                GasUsed = (long)wire1.GasUsed,
                Timestamp = wire1.Timestamp,
                ExtraData = wire1.ExtraData,
                BaseFeePerGas = wire1.BaseFeePerGas,
                BlockHash = new Hash256(wire1.BlockHash),
                Transactions = TransactionsFromWire(wire1.Transactions)
            };
        }

        // version 2
        SszEncoding.Decode(buf, out ExecutionPayloadV2Wire wire2);
        return new ExecutionPayloadV3
        {
            ParentHash = new Hash256(wire2.ParentHash),
            FeeRecipient = new Address(wire2.FeeRecipient),
            StateRoot = new Hash256(wire2.StateRoot),
            ReceiptsRoot = new Hash256(wire2.ReceiptsRoot),
            LogsBloom = new Bloom(wire2.LogsBloom),
            PrevRandao = new Hash256(wire2.PrevRandao),
            BlockNumber = (long)wire2.BlockNumber,
            GasLimit = (long)wire2.GasLimit,
            GasUsed = (long)wire2.GasUsed,
            Timestamp = wire2.Timestamp,
            ExtraData = wire2.ExtraData,
            BaseFeePerGas = wire2.BaseFeePerGas,
            BlockHash = new Hash256(wire2.BlockHash),
            Transactions = TransactionsFromWire(wire2.Transactions),
            Withdrawals = WithdrawalsFromWireArray(wire2.Withdrawals)
        };
    }

    private static WithdrawalWire[] WithdrawalsToWireArray(Withdrawal[]? withdrawals)
    {
        if (withdrawals is null || withdrawals.Length == 0) return [];
        WithdrawalWire[] result = new WithdrawalWire[withdrawals.Length];
        for (int i = 0; i < withdrawals.Length; i++)
            result[i] = WithdrawalToWire(withdrawals[i]);
        return result;
    }

    private static Withdrawal[] WithdrawalsFromWireArray(WithdrawalWire[] wires)
    {
        if (wires is null || wires.Length == 0) return [];
        Withdrawal[] result = new Withdrawal[wires.Length];
        for (int i = 0; i < wires.Length; i++)
            result[i] = WithdrawalFromWire(wires[i]);
        return result;
    }

    private static BlobsBundleWire BlobsBundleToWire(BlobsBundleV1? bundle)
    {
        if (bundle is null)
            return new BlobsBundleWire { Commitments = [], Proofs = [], Blobs = [] };

        SszKzgCommitment[] commitments = new SszKzgCommitment[bundle.Commitments.Length];
        for (int i = 0; i < bundle.Commitments.Length; i++)
            commitments[i] = new SszKzgCommitment { Bytes = bundle.Commitments[i] };

        SszKzgProof[] proofs = new SszKzgProof[bundle.Proofs.Length];
        for (int i = 0; i < bundle.Proofs.Length; i++)
            proofs[i] = new SszKzgProof { Bytes = bundle.Proofs[i] };

        SszBlob[] blobs = new SszBlob[bundle.Blobs.Length];
        for (int i = 0; i < bundle.Blobs.Length; i++)
            blobs[i] = new SszBlob { Bytes = bundle.Blobs[i] };

        return new BlobsBundleWire { Commitments = commitments, Proofs = proofs, Blobs = blobs };
    }

    private static ExecutionRequestsWire ExecutionRequestsToWire(byte[][]? reqs)
    {
        byte[] depositsData = [];
        byte[] withdrawalsData = [];
        byte[] consolidationsData = [];

        if (reqs is not null)
        {
            foreach (byte[] r in reqs)
            {
                if (r.Length < 1) continue;
                byte[] data = r[1..];
                switch (r[0])
                {
                    case 0x00: depositsData = data; break;
                    case 0x01: withdrawalsData = data; break;
                    case 0x02: consolidationsData = data; break;
                }
            }
        }

        return new ExecutionRequestsWire
        {
            Deposits = depositsData,
            Withdrawals = withdrawalsData,
            Consolidations = consolidationsData
        };
    }

    private static byte[][] ExecutionRequestsFromWire(ExecutionRequestsWire wire)
    {
        List<byte[]> reqs = new();

        if (wire.Deposits is { Length: > 0 })
        {
            byte[] r = new byte[1 + wire.Deposits.Length];
            r[0] = 0x00;
            wire.Deposits.CopyTo(r, 1);
            reqs.Add(r);
        }

        if (wire.Withdrawals is { Length: > 0 })
        {
            byte[] r = new byte[1 + wire.Withdrawals.Length];
            r[0] = 0x01;
            wire.Withdrawals.CopyTo(r, 1);
            reqs.Add(r);
        }

        if (wire.Consolidations is { Length: > 0 })
        {
            byte[] r = new byte[1 + wire.Consolidations.Length];
            r[0] = 0x02;
            wire.Consolidations.CopyTo(r, 1);
            reqs.Add(r);
        }

        return reqs.ToArray();
    }

    #endregion
}

public sealed class SszDecodingException : Exception
{
    public SszDecodingException(string message) : base(message) { }
}
