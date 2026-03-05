// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Consensus.Producers;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Merge.Plugin.SszRest;

/// <summary>
/// Hand-rolled SSZ encoding/decoding for EIP-8161 SSZ-REST Engine API transport.
/// Wire format matches geth's beacon/engine/ssz.go exactly.
/// </summary>
public static class SszEncoding
{
    // SSZ status codes matching geth's EIP-8161 encoding.
    private const byte SszStatusValid = 0;
    private const byte SszStatusInvalid = 1;
    private const byte SszStatusSyncing = 2;
    private const byte SszStatusAccepted = 3;
    private const byte SszStatusInvalidBlockHash = 4;

    private const int PayloadStatusFixedSize = 9; // status(1) + hash_offset(4) + err_offset(4)
    private const int ForkchoiceUpdatedResponseFixedSize = 8;
    private const int WithdrawalSszSize = 44; // index(8) + validator_index(8) + address(20) + amount(8)
    private const int GetPayloadResponseFixedSize = 45; // ep_offset(4) + block_value(32) + blobs_offset(4) + override(1) + requests_offset(4)
    private const int BlobsBundleFixedSize = 12; // 3 offsets

    #region PayloadStatus

    /// <summary>
    /// Encodes a <see cref="PayloadStatusV1"/> to SSZ bytes per EIP-8161.
    /// </summary>
    public static byte[] EncodePayloadStatus(PayloadStatusV1 ps)
    {
        byte[] hashUnion;
        if (ps.LatestValidHash is not null)
        {
            hashUnion = new byte[33]; // selector(1) + hash(32)
            hashUnion[0] = 1;
            ps.LatestValidHash.Bytes.CopyTo(hashUnion.AsSpan(1, 32));
        }
        else
        {
            hashUnion = [0];
        }

        byte[] errorBytes = ps.ValidationError is not null
            ? Encoding.UTF8.GetBytes(ps.ValidationError)
            : [];

        byte[] buf = new byte[PayloadStatusFixedSize + hashUnion.Length + errorBytes.Length];
        buf[0] = EngineStatusToSsz(ps.Status);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(1, 4), (uint)PayloadStatusFixedSize);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(5, 4), (uint)(PayloadStatusFixedSize + hashUnion.Length));

        hashUnion.CopyTo(buf.AsSpan(PayloadStatusFixedSize));
        errorBytes.CopyTo(buf.AsSpan(PayloadStatusFixedSize + hashUnion.Length));
        return buf;
    }

    /// <summary>
    /// Decodes SSZ bytes into a <see cref="PayloadStatusV1"/>.
    /// </summary>
    public static PayloadStatusV1 DecodePayloadStatus(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < PayloadStatusFixedSize)
            throw new SszDecodingException($"PayloadStatus: buffer too short ({buf.Length} < {PayloadStatusFixedSize})");

        PayloadStatusV1 ps = new() { Status = SszToEngineStatus(buf[0]) };

        uint hashOffset = BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(1, 4));
        uint errOffset = BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(5, 4));

        if (hashOffset > (uint)buf.Length || errOffset > (uint)buf.Length || hashOffset > errOffset)
            throw new SszDecodingException("PayloadStatus: offsets out of bounds");

        ReadOnlySpan<byte> unionData = buf[(int)hashOffset..(int)errOffset];
        if (unionData.Length > 0 && unionData[0] == 1)
        {
            if (unionData.Length < 33)
                throw new SszDecodingException("PayloadStatus: Union hash data too short");
            ps.LatestValidHash = new Hash256(unionData.Slice(1, 32));
        }

        if (errOffset < (uint)buf.Length)
        {
            ps.ValidationError = Encoding.UTF8.GetString(buf[(int)errOffset..]);
        }

        return ps;
    }

    #endregion

    #region ForkchoiceState

    /// <summary>
    /// Decodes a 96-byte <see cref="ForkchoiceStateV1"/> from SSZ bytes.
    /// </summary>
    public static ForkchoiceStateV1 DecodeForkchoiceState(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < 96)
            throw new SszDecodingException($"ForkchoiceState: buffer too short ({buf.Length} < 96)");

        return new ForkchoiceStateV1(
            headBlockHash: new Hash256(buf[..32]),
            finalizedBlockHash: new Hash256(buf[64..96]),
            safeBlockHash: new Hash256(buf[32..64])
        );
    }

    #endregion

    #region ForkchoiceUpdated Response

    /// <summary>
    /// Encodes a <see cref="ForkchoiceUpdatedV1Result"/> to SSZ bytes.
    /// </summary>
    public static byte[] EncodeForkchoiceUpdatedResponse(ForkchoiceUpdatedV1Result resp)
    {
        byte[] psBytes = EncodePayloadStatus(resp.PayloadStatus);

        byte[] pidUnion;
        if (resp.PayloadId is not null)
        {
            byte[] payloadIdBytes = Convert.FromHexString(resp.PayloadId.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? resp.PayloadId[2..]
                : resp.PayloadId);
            pidUnion = new byte[9]; // selector(1) + 8 bytes
            pidUnion[0] = 1;
            payloadIdBytes.AsSpan(0, Math.Min(8, payloadIdBytes.Length)).CopyTo(pidUnion.AsSpan(1));
        }
        else
        {
            pidUnion = [0];
        }

        byte[] buf = new byte[ForkchoiceUpdatedResponseFixedSize + psBytes.Length + pidUnion.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0, 4), (uint)ForkchoiceUpdatedResponseFixedSize);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4, 4), (uint)(ForkchoiceUpdatedResponseFixedSize + psBytes.Length));

        psBytes.CopyTo(buf.AsSpan(ForkchoiceUpdatedResponseFixedSize));
        pidUnion.CopyTo(buf.AsSpan(ForkchoiceUpdatedResponseFixedSize + psBytes.Length));
        return buf;
    }

    #endregion

    #region ForkchoiceUpdated Request

    /// <summary>
    /// Decodes a forkchoiceUpdated SSZ request into forkchoice state and optional payload attributes.
    /// </summary>
    public static (ForkchoiceStateV1 state, PayloadAttributes? attributes) DecodeForkchoiceUpdatedRequest(ReadOnlySpan<byte> buf, int version)
    {
        if (buf.Length < 100)
            throw new SszDecodingException($"ForkchoiceUpdatedRequest: buffer too short ({buf.Length} < 100)");

        ForkchoiceStateV1 state = new(
            headBlockHash: new Hash256(buf[..32]),
            finalizedBlockHash: new Hash256(buf[64..96]),
            safeBlockHash: new Hash256(buf[32..64])
        );

        uint paOffset = BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(96, 4));
        if (paOffset > (uint)buf.Length)
            throw new SszDecodingException("ForkchoiceUpdatedRequest: payload attributes offset out of bounds");

        PayloadAttributes? attributes = null;
        if (paOffset < (uint)buf.Length)
        {
            ReadOnlySpan<byte> unionData = buf[(int)paOffset..];
            if (unionData.Length > 0 && unionData[0] == 1)
            {
                attributes = DecodePayloadAttributes(unionData[1..], version);
            }
        }

        return (state, attributes);
    }

    #endregion

    #region PayloadAttributes

    /// <summary>
    /// Decodes <see cref="PayloadAttributes"/> from SSZ bytes.
    /// </summary>
    public static PayloadAttributes DecodePayloadAttributes(ReadOnlySpan<byte> buf, int version)
    {
        if (buf.Length < 60)
            throw new SszDecodingException($"PayloadAttributes: buffer too short ({buf.Length} < 60)");

        PayloadAttributes pa = new()
        {
            Timestamp = BinaryPrimitives.ReadUInt64LittleEndian(buf[..8]),
            PrevRandao = new Hash256(buf.Slice(8, 32)),
            SuggestedFeeRecipient = new Address(buf.Slice(40, 20))
        };

        if (version == 1)
            return pa;

        if (buf.Length < 64)
            throw new SszDecodingException($"PayloadAttributes V2+: buffer too short ({buf.Length} < 64)");

        uint withdrawalsOffset = BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(60, 4));

        if (version >= 3)
        {
            if (buf.Length < 96)
                throw new SszDecodingException($"PayloadAttributes V3: buffer too short ({buf.Length} < 96)");
            pa.ParentBeaconBlockRoot = new Hash256(buf.Slice(64, 32));
        }

        if (withdrawalsOffset <= (uint)buf.Length)
        {
            ReadOnlySpan<byte> wdBuf = buf[(int)withdrawalsOffset..];
            if (wdBuf.Length > 0)
            {
                if (wdBuf.Length % WithdrawalSszSize != 0)
                    throw new SszDecodingException($"PayloadAttributes: withdrawals buffer length {wdBuf.Length} not divisible by {WithdrawalSszSize}");

                int count = wdBuf.Length / WithdrawalSszSize;
                pa.Withdrawals = new Withdrawal[count];
                for (int i = 0; i < count; i++)
                {
                    int off = i * WithdrawalSszSize;
                    pa.Withdrawals[i] = new Withdrawal
                    {
                        Index = BinaryPrimitives.ReadUInt64LittleEndian(wdBuf.Slice(off, 8)),
                        ValidatorIndex = BinaryPrimitives.ReadUInt64LittleEndian(wdBuf.Slice(off + 8, 8)),
                        Address = new Address(wdBuf.Slice(off + 16, 20)),
                        AmountInGwei = BinaryPrimitives.ReadUInt64LittleEndian(wdBuf.Slice(off + 36, 8))
                    };
                }
            }
            else
            {
                pa.Withdrawals = [];
            }
        }

        return pa;
    }

    #endregion

    #region NewPayload Request

    /// <summary>
    /// Decodes a newPayload SSZ request. Returns the execution payload, versioned hashes,
    /// parent beacon block root, and execution requests.
    /// </summary>
    public static (ExecutionPayloadV3 payload, byte[]?[] versionedHashes, Hash256? parentBeaconBlockRoot, byte[][]? executionRequests) DecodeNewPayloadRequest(ReadOnlySpan<byte> buf, int version)
    {
        int payloadVersion = EngineVersionToPayloadVersion(version);

        if (version <= 2)
        {
            ExecutionPayloadV3 ep = DecodeExecutionPayload(buf, payloadVersion);
            return (ep, [], null, null);
        }

        if (version == 3)
        {
            if (buf.Length < 40)
                throw new SszDecodingException($"NewPayloadV3: buffer too short ({buf.Length} < 40)");

            uint epOffset = BinaryPrimitives.ReadUInt32LittleEndian(buf[..4]);
            uint blobHashOffset = BinaryPrimitives.ReadUInt32LittleEndian(buf[4..8]);
            Hash256 root = new(buf.Slice(8, 32));

            if (epOffset > (uint)buf.Length || blobHashOffset > (uint)buf.Length || epOffset > blobHashOffset)
                throw new SszDecodingException("NewPayloadV3: invalid offsets");

            ExecutionPayloadV3 ep = DecodeExecutionPayload(buf[(int)epOffset..(int)blobHashOffset], payloadVersion);
            byte[]?[] hashes = DecodeBlobVersionedHashes(buf[(int)blobHashOffset..]);
            return (ep, hashes, root, null);
        }

        // V4+
        if (buf.Length < 44)
            throw new SszDecodingException($"NewPayloadV4: buffer too short ({buf.Length} < 44)");

        uint epOff4 = BinaryPrimitives.ReadUInt32LittleEndian(buf[..4]);
        uint blobOff4 = BinaryPrimitives.ReadUInt32LittleEndian(buf[4..8]);
        Hash256 root4 = new(buf.Slice(8, 32));
        uint reqOff4 = BinaryPrimitives.ReadUInt32LittleEndian(buf[40..44]);

        if (epOff4 > (uint)buf.Length || blobOff4 > (uint)buf.Length || reqOff4 > (uint)buf.Length)
            throw new SszDecodingException("NewPayloadV4: offsets out of bounds");

        ExecutionPayloadV3 ep4 = DecodeExecutionPayload(buf[(int)epOff4..(int)blobOff4], payloadVersion);
        byte[]?[] hashes4 = DecodeBlobVersionedHashes(buf[(int)blobOff4..(int)reqOff4]);
        byte[][] reqs = DecodeStructuredExecutionRequests(buf[(int)reqOff4..]);
        return (ep4, hashes4, root4, reqs);
    }

    #endregion

    #region ExecutionPayload

    /// <summary>
    /// Returns the fixed part size for a given execution payload version.
    /// Matches geth's executionPayloadFixedSize exactly.
    /// </summary>
    private static int ExecutionPayloadFixedSize(int version)
    {
        int size = 508; // V1 base
        if (version >= 2) size += 4; // withdrawals_offset
        if (version >= 3) size += 8 + 8; // blob_gas_used + excess_blob_gas
        if (version >= 4) size += 8 + 4; // slot_number + block_access_list_offset
        return size;
    }

    private static int EngineVersionToPayloadVersion(int engineVersion)
    {
        if (engineVersion == 4) return 3; // Electra uses Deneb payload layout
        if (engineVersion >= 5) return 4;
        return engineVersion;
    }

    private static ExecutionPayloadV3 DecodeExecutionPayload(ReadOnlySpan<byte> buf, int version)
    {
        int fixedSize = ExecutionPayloadFixedSize(version);
        if (buf.Length < fixedSize)
            throw new SszDecodingException($"ExecutionPayload: buffer too short ({buf.Length} < {fixedSize})");

        ExecutionPayloadV3 ep = new();
        int pos = 0;

        ep.ParentHash = new Hash256(buf.Slice(pos, 32)); pos += 32;
        ep.FeeRecipient = new Address(buf.Slice(pos, 20)); pos += 20;
        ep.StateRoot = new Hash256(buf.Slice(pos, 32)); pos += 32;
        ep.ReceiptsRoot = new Hash256(buf.Slice(pos, 32)); pos += 32;
        ep.LogsBloom = new Bloom(buf.Slice(pos, 256)); pos += 256;
        ep.PrevRandao = new Hash256(buf.Slice(pos, 32)); pos += 32;
        ep.BlockNumber = (long)BinaryPrimitives.ReadUInt64LittleEndian(buf.Slice(pos, 8)); pos += 8;
        ep.GasLimit = (long)BinaryPrimitives.ReadUInt64LittleEndian(buf.Slice(pos, 8)); pos += 8;
        ep.GasUsed = (long)BinaryPrimitives.ReadUInt64LittleEndian(buf.Slice(pos, 8)); pos += 8;
        ep.Timestamp = BinaryPrimitives.ReadUInt64LittleEndian(buf.Slice(pos, 8)); pos += 8;

        uint extraDataOffset = BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(pos, 4)); pos += 4;

        ep.BaseFeePerGas = SszBytesToUInt256(buf.Slice(pos, 32)); pos += 32;

        ep.BlockHash = new Hash256(buf.Slice(pos, 32)); pos += 32;

        uint txOffset = BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(pos, 4)); pos += 4;

        uint wdOffset = 0;
        if (version >= 2)
        {
            wdOffset = BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(pos, 4)); pos += 4;
        }

        if (version >= 3)
        {
            ulong blobGasUsed = BinaryPrimitives.ReadUInt64LittleEndian(buf.Slice(pos, 8)); pos += 8;
            ulong excessBlobGas = BinaryPrimitives.ReadUInt64LittleEndian(buf.Slice(pos, 8)); pos += 8;
            ep.BlobGasUsed = blobGasUsed;
            ep.ExcessBlobGas = excessBlobGas;
        }

        // Decode variable-length fields
        if (extraDataOffset > (uint)buf.Length || txOffset > (uint)buf.Length || extraDataOffset > txOffset)
            throw new SszDecodingException("ExecutionPayload: invalid extra_data/transactions offsets");

        ep.ExtraData = buf[(int)extraDataOffset..(int)txOffset].ToArray();

        uint txEnd = version >= 2 ? wdOffset : (uint)buf.Length;
        if (txOffset > txEnd)
            throw new SszDecodingException("ExecutionPayload: transactions offset > end");

        ep.Transactions = DecodeTransactions(buf[(int)txOffset..(int)txEnd]);

        if (version >= 2)
        {
            uint wdEnd = (uint)buf.Length;
            if (wdOffset > wdEnd)
                throw new SszDecodingException("ExecutionPayload: invalid withdrawals offset");
            ep.Withdrawals = DecodeWithdrawals(buf[(int)wdOffset..(int)wdEnd]);
        }

        return ep;
    }

    /// <summary>
    /// Encodes an <see cref="ExecutionPayload"/> to SSZ bytes.
    /// </summary>
    public static byte[] EncodeExecutionPayload(ExecutionPayload ep, int version)
    {
        int fixedSize = ExecutionPayloadFixedSize(version);

        byte[] extraData = ep.ExtraData ?? [];
        byte[] txData = EncodeTransactions(ep.Transactions);
        byte[] withdrawalData = version >= 2 ? EncodeWithdrawals(ep.Withdrawals) : [];

        int totalVarSize = extraData.Length + txData.Length;
        if (version >= 2) totalVarSize += withdrawalData.Length;

        byte[] buf = new byte[fixedSize + totalVarSize];
        int pos = 0;

        // Fixed fields
        ep.ParentHash.Bytes.CopyTo(buf.AsSpan(pos, 32)); pos += 32;
        ep.FeeRecipient.Bytes.CopyTo(buf.AsSpan(pos, 20)); pos += 20;
        ep.StateRoot.Bytes.CopyTo(buf.AsSpan(pos, 32)); pos += 32;
        ep.ReceiptsRoot.Bytes.CopyTo(buf.AsSpan(pos, 32)); pos += 32;
        ep.LogsBloom.Bytes.CopyTo(buf.AsSpan(pos, 256)); pos += 256;
        ep.PrevRandao.Bytes.CopyTo(buf.AsSpan(pos, 32)); pos += 32;
        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(pos, 8), (ulong)ep.BlockNumber); pos += 8;
        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(pos, 8), (ulong)ep.GasLimit); pos += 8;
        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(pos, 8), (ulong)ep.GasUsed); pos += 8;
        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(pos, 8), ep.Timestamp); pos += 8;

        // extra_data offset
        int extraDataOffset = fixedSize;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(pos, 4), (uint)extraDataOffset); pos += 4;

        // base_fee_per_gas (uint256, 32 bytes LE)
        UInt256ToSszBytes(ep.BaseFeePerGas, buf.AsSpan(pos, 32)); pos += 32;

        ep.BlockHash.Bytes.CopyTo(buf.AsSpan(pos, 32)); pos += 32;

        // transactions offset
        int txOffset = extraDataOffset + extraData.Length;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(pos, 4), (uint)txOffset); pos += 4;

        if (version >= 2)
        {
            int wdOffset = txOffset + txData.Length;
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(pos, 4), (uint)wdOffset); pos += 4;
        }

        if (version >= 3)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(pos, 8), ep.BlobGasUsed ?? 0); pos += 8;
            BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(pos, 8), ep.ExcessBlobGas ?? 0); pos += 8;
        }

        // Variable part
        extraData.CopyTo(buf.AsSpan(extraDataOffset));
        txData.CopyTo(buf.AsSpan(txOffset));
        if (version >= 2)
        {
            int wdOffset = txOffset + txData.Length;
            withdrawalData.CopyTo(buf.AsSpan(wdOffset));
        }

        return buf;
    }

    #endregion

    #region GetPayload Response

    /// <summary>
    /// Encodes a GetPayloadV4Result to SSZ bytes.
    /// </summary>
    public static byte[] EncodeGetPayloadResponse(ExecutionPayload ep, UInt256 blockValue, BlobsBundleV1? blobsBundle, bool shouldOverrideBuilder, byte[][]? executionRequests, int version)
    {
        if (version == 1)
            return EncodeExecutionPayload(ep, 1);

        int payloadVersion = EngineVersionToPayloadVersion(version);
        byte[] epBytes = EncodeExecutionPayload(ep, payloadVersion);
        byte[] blobsBytes = EncodeBlobsBundle(blobsBundle);
        byte[] reqBytes = EncodeStructuredExecutionRequests(executionRequests);

        byte[] buf = new byte[GetPayloadResponseFixedSize + epBytes.Length + blobsBytes.Length + reqBytes.Length];

        // ep offset
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0, 4), (uint)GetPayloadResponseFixedSize);

        // block_value (uint256 LE)
        UInt256ToSszBytes(blockValue, buf.AsSpan(4, 32));

        // blobs_bundle offset
        int blobsOffset = GetPayloadResponseFixedSize + epBytes.Length;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(36, 4), (uint)blobsOffset);

        // should_override_builder
        buf[40] = shouldOverrideBuilder ? (byte)1 : (byte)0;

        // execution_requests offset
        int reqOffset = blobsOffset + blobsBytes.Length;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(41, 4), (uint)reqOffset);

        // Variable data
        epBytes.CopyTo(buf.AsSpan(GetPayloadResponseFixedSize));
        blobsBytes.CopyTo(buf.AsSpan(blobsOffset));
        reqBytes.CopyTo(buf.AsSpan(reqOffset));

        return buf;
    }

    #endregion

    #region Capabilities

    /// <summary>
    /// Encodes a list of capability strings to SSZ matching geth's format:
    /// count(4) + [len(4) + data]...
    /// </summary>
    public static byte[] EncodeCapabilities(IEnumerable<string> capabilities)
    {
        List<byte[]> parts = new();
        foreach (string cap in capabilities)
            parts.Add(Encoding.UTF8.GetBytes(cap));

        int totalSize = 4; // count
        foreach (byte[] p in parts)
            totalSize += 4 + p.Length;

        byte[] buf = new byte[totalSize];
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0, 4), (uint)parts.Count);

        int offset = 4;
        foreach (byte[] p in parts)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(offset, 4), (uint)p.Length);
            offset += 4;
            p.CopyTo(buf.AsSpan(offset));
            offset += p.Length;
        }

        return buf;
    }

    /// <summary>
    /// Decodes a list of capability strings from SSZ bytes.
    /// </summary>
    public static string[] DecodeCapabilities(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < 4)
            throw new SszDecodingException("Capabilities: buffer too short");

        uint count = BinaryPrimitives.ReadUInt32LittleEndian(buf[..4]);
        if (count > 128)
            throw new SszDecodingException($"Capabilities: too many ({count} > 128)");

        string[] result = new string[count];
        int offset = 4;

        for (int i = 0; i < (int)count; i++)
        {
            if (offset + 4 > buf.Length)
                throw new SszDecodingException("Capabilities: unexpected end of buffer");

            uint capLen = BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(offset, 4));
            offset += 4;

            if (capLen > 64 || offset + (int)capLen > buf.Length)
                throw new SszDecodingException("Capabilities: capability too long or truncated");

            result[i] = Encoding.UTF8.GetString(buf.Slice(offset, (int)capLen));
            offset += (int)capLen;
        }

        return result;
    }

    #endregion

    #region ClientVersion

    /// <summary>
    /// Encodes a single <see cref="ClientVersionV1"/> to SSZ matching geth's format:
    /// [len(4) + data] for each of code, name, version, commit.
    /// </summary>
    public static byte[] EncodeClientVersion(ClientVersionV1 cv)
    {
        byte[] codeBytes = Encoding.UTF8.GetBytes(cv.Code);
        byte[] nameBytes = Encoding.UTF8.GetBytes(cv.Name);
        byte[] versionBytes = Encoding.UTF8.GetBytes(cv.Version);
        byte[] commitBytes = Encoding.UTF8.GetBytes(cv.Commit);

        int totalLen = 4 + codeBytes.Length + 4 + nameBytes.Length + 4 + versionBytes.Length + 4 + commitBytes.Length;
        byte[] buf = new byte[totalLen];

        int offset = 0;
        foreach (byte[] field in new[] { codeBytes, nameBytes, versionBytes, commitBytes })
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(offset, 4), (uint)field.Length);
            offset += 4;
            field.CopyTo(buf.AsSpan(offset));
            offset += field.Length;
        }

        return buf;
    }

    /// <summary>
    /// Encodes a list of <see cref="ClientVersionV1"/> to SSZ:
    /// count(4) + [len(4) + encoded_cv]...
    /// </summary>
    public static byte[] EncodeClientVersions(ClientVersionV1[] versions)
    {
        List<byte[]> parts = new();
        foreach (ClientVersionV1 cv in versions)
            parts.Add(EncodeClientVersion(cv));

        int totalLen = 4;
        foreach (byte[] p in parts)
            totalLen += 4 + p.Length;

        byte[] buf = new byte[totalLen];
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0, 4), (uint)versions.Length);

        int offset = 4;
        foreach (byte[] p in parts)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(offset, 4), (uint)p.Length);
            offset += 4;
            p.CopyTo(buf.AsSpan(offset));
            offset += p.Length;
        }

        return buf;
    }

    /// <summary>
    /// Decodes a <see cref="ClientVersionV1"/> from SSZ bytes (for the request side).
    /// </summary>
    public static ClientVersionV1 DecodeClientVersion(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < 16)
            throw new SszDecodingException("ClientVersion: buffer too short");

        int offset = 0;

        // The incoming client version is from the CL - we just need to parse it
        // but then we return our own client version, so this is mostly for completeness.
        // Parse 4 length-prefixed string fields: code, name, version, commit
        int[] maxLens = [8, 64, 64, 64];
        for (int i = 0; i < 4; i++)
        {
            if (offset + 4 > buf.Length)
                throw new SszDecodingException("ClientVersion: unexpected end of buffer");
            uint len = BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(offset, 4));
            offset += 4;
            if (len > (uint)maxLens[i] || offset + (int)len > buf.Length)
                throw new SszDecodingException("ClientVersion: field too long or truncated");
            offset += (int)len;
        }

        // Return our own client version (same as JSON-RPC behavior)
        return new ClientVersionV1();
    }

    /// <summary>
    /// Decodes client versions list from SSZ bytes.
    /// </summary>
    public static ClientVersionV1 DecodeClientVersions(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < 4)
            throw new SszDecodingException("ClientVersions: buffer too short");

        uint count = BinaryPrimitives.ReadUInt32LittleEndian(buf[..4]);
        if (count > 16)
            throw new SszDecodingException($"ClientVersions: too many ({count} > 16)");

        // We just need to parse one and return our own.
        // The CL sends its own version, we respond with ours.
        return new ClientVersionV1();
    }

    #endregion

    #region GetBlobs Request

    /// <summary>
    /// Decodes a list of versioned hashes from a getBlobsV1 SSZ request.
    /// </summary>
    public static byte[][] DecodeGetBlobsRequest(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < 4)
            throw new SszDecodingException("GetBlobsRequest: buffer too short");

        uint count = BinaryPrimitives.ReadUInt32LittleEndian(buf[..4]);
        if (4 + count * 32 > (uint)buf.Length)
            throw new SszDecodingException($"GetBlobsRequest: buffer too short for {count} hashes");

        byte[][] hashes = new byte[count][];
        for (int i = 0; i < (int)count; i++)
        {
            hashes[i] = buf.Slice(4 + i * 32, 32).ToArray();
        }

        return hashes;
    }

    #endregion

    #region GetPayload Request (just a payload ID)

    /// <summary>
    /// Decodes a getPayload SSZ request (just a payload ID as raw bytes).
    /// </summary>
    public static byte[] DecodeGetPayloadRequest(ReadOnlySpan<byte> buf)
    {
        // The payload ID is sent as raw 8 bytes
        if (buf.Length < 8)
            throw new SszDecodingException($"GetPayloadRequest: buffer too short ({buf.Length} < 8)");
        return buf[..8].ToArray();
    }

    #endregion

    #region Private helpers

    private static byte EngineStatusToSsz(string status)
    {
        return status switch
        {
            PayloadStatus.Valid => SszStatusValid,
            PayloadStatus.Invalid => SszStatusInvalid,
            PayloadStatus.Syncing => SszStatusSyncing,
            PayloadStatus.Accepted => SszStatusAccepted,
            "INVALID_BLOCK_HASH" => SszStatusInvalidBlockHash,
            _ => SszStatusInvalid
        };
    }

    private static string SszToEngineStatus(byte status)
    {
        return status switch
        {
            SszStatusValid => PayloadStatus.Valid,
            SszStatusInvalid => PayloadStatus.Invalid,
            SszStatusSyncing => PayloadStatus.Syncing,
            SszStatusAccepted => PayloadStatus.Accepted,
            SszStatusInvalidBlockHash => "INVALID_BLOCK_HASH",
            _ => PayloadStatus.Invalid
        };
    }

    private static UInt256 SszBytesToUInt256(ReadOnlySpan<byte> buf)
    {
        // SSZ uint256 is 32 bytes little-endian
        return new UInt256(buf, isBigEndian: false);
    }

    private static void UInt256ToSszBytes(UInt256 val, Span<byte> buf)
    {
        // Write 4 ulongs in little-endian order (u0 at offset 0, u3 at offset 24)
        BinaryPrimitives.WriteUInt64LittleEndian(buf[..8], val.u0);
        BinaryPrimitives.WriteUInt64LittleEndian(buf[8..16], val.u1);
        BinaryPrimitives.WriteUInt64LittleEndian(buf[16..24], val.u2);
        BinaryPrimitives.WriteUInt64LittleEndian(buf[24..32], val.u3);
    }

    private static byte[][] DecodeTransactions(ReadOnlySpan<byte> buf)
    {
        if (buf.Length == 0)
            return [];

        if (buf.Length < 4)
            throw new SszDecodingException("Transactions: buffer too short");

        uint firstOffset = BinaryPrimitives.ReadUInt32LittleEndian(buf[..4]);
        if (firstOffset % 4 != 0)
            throw new SszDecodingException($"Transactions: first offset not aligned ({firstOffset})");

        uint count = firstOffset / 4;
        if (count == 0) return [];
        if (firstOffset > (uint)buf.Length)
            throw new SszDecodingException("Transactions: first offset out of bounds");

        uint[] offsets = new uint[count];
        for (int i = 0; i < (int)count; i++)
        {
            offsets[i] = BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(i * 4, 4));
        }

        byte[][] txs = new byte[count][];
        for (int i = 0; i < (int)count; i++)
        {
            uint start = offsets[i];
            uint end = i + 1 < (int)count ? offsets[i + 1] : (uint)buf.Length;
            if (start > (uint)buf.Length || end > (uint)buf.Length || start > end)
                throw new SszDecodingException($"Transactions: invalid offset at index {i}");
            txs[i] = buf[(int)start..(int)end].ToArray();
        }

        return txs;
    }

    private static byte[] EncodeTransactions(byte[][] txs)
    {
        if (txs is null || txs.Length == 0)
            return [];

        int offsetsSize = txs.Length * 4;
        int dataSize = 0;
        foreach (byte[] tx in txs)
            dataSize += tx.Length;

        byte[] buf = new byte[offsetsSize + dataSize];
        int dataStart = offsetsSize;
        for (int i = 0; i < txs.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(i * 4, 4), (uint)dataStart);
            dataStart += txs[i].Length;
        }

        int pos = offsetsSize;
        foreach (byte[] tx in txs)
        {
            tx.CopyTo(buf.AsSpan(pos));
            pos += tx.Length;
        }

        return buf;
    }

    private static Withdrawal[] DecodeWithdrawals(ReadOnlySpan<byte> buf)
    {
        if (buf.Length == 0) return [];

        if (buf.Length % WithdrawalSszSize != 0)
            throw new SszDecodingException($"Withdrawals: buffer length {buf.Length} not divisible by {WithdrawalSszSize}");

        int count = buf.Length / WithdrawalSszSize;
        Withdrawal[] withdrawals = new Withdrawal[count];
        for (int i = 0; i < count; i++)
        {
            int off = i * WithdrawalSszSize;
            withdrawals[i] = new Withdrawal
            {
                Index = BinaryPrimitives.ReadUInt64LittleEndian(buf.Slice(off, 8)),
                ValidatorIndex = BinaryPrimitives.ReadUInt64LittleEndian(buf.Slice(off + 8, 8)),
                Address = new Address(buf.Slice(off + 16, 20)),
                AmountInGwei = BinaryPrimitives.ReadUInt64LittleEndian(buf.Slice(off + 36, 8))
            };
        }

        return withdrawals;
    }

    private static byte[] EncodeWithdrawals(Withdrawal[]? withdrawals)
    {
        if (withdrawals is null) return [];

        byte[] buf = new byte[withdrawals.Length * WithdrawalSszSize];
        for (int i = 0; i < withdrawals.Length; i++)
        {
            int off = i * WithdrawalSszSize;
            BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(off, 8), withdrawals[i].Index);
            BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(off + 8, 8), withdrawals[i].ValidatorIndex);
            withdrawals[i].Address.Bytes.CopyTo(buf.AsSpan(off + 16, 20));
            BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(off + 36, 8), withdrawals[i].AmountInGwei);
        }

        return buf;
    }

    private static byte[]?[] DecodeBlobVersionedHashes(ReadOnlySpan<byte> buf)
    {
        if (buf.Length == 0) return [];

        if (buf.Length % 32 != 0)
            throw new SszDecodingException("Blob versioned hashes: not aligned to 32 bytes");

        int count = buf.Length / 32;
        byte[]?[] hashes = new byte[count][];
        for (int i = 0; i < count; i++)
        {
            hashes[i] = buf.Slice(i * 32, 32).ToArray();
        }

        return hashes;
    }

    private static byte[][] DecodeStructuredExecutionRequests(ReadOnlySpan<byte> buf)
    {
        if (buf.Length == 0) return [];

        if (buf.Length < 12)
            throw new SszDecodingException($"Structured execution requests: buffer too short ({buf.Length} < 12)");

        uint depositsOffset = BinaryPrimitives.ReadUInt32LittleEndian(buf[..4]);
        uint withdrawalsOffset = BinaryPrimitives.ReadUInt32LittleEndian(buf[4..8]);
        uint consolidationsOffset = BinaryPrimitives.ReadUInt32LittleEndian(buf[8..12]);

        if (depositsOffset > (uint)buf.Length || withdrawalsOffset > (uint)buf.Length || consolidationsOffset > (uint)buf.Length)
            throw new SszDecodingException("Structured execution requests: offsets out of bounds");
        if (depositsOffset > withdrawalsOffset || withdrawalsOffset > consolidationsOffset)
            throw new SszDecodingException("Structured execution requests: offsets not in order");

        List<byte[]> reqs = new();

        ReadOnlySpan<byte> depositsData = buf[(int)depositsOffset..(int)withdrawalsOffset];
        if (depositsData.Length > 0)
        {
            byte[] r = new byte[1 + depositsData.Length];
            r[0] = 0x00;
            depositsData.CopyTo(r.AsSpan(1));
            reqs.Add(r);
        }

        ReadOnlySpan<byte> withdrawalsData = buf[(int)withdrawalsOffset..(int)consolidationsOffset];
        if (withdrawalsData.Length > 0)
        {
            byte[] r = new byte[1 + withdrawalsData.Length];
            r[0] = 0x01;
            withdrawalsData.CopyTo(r.AsSpan(1));
            reqs.Add(r);
        }

        ReadOnlySpan<byte> consolidationsData = buf[(int)consolidationsOffset..];
        if (consolidationsData.Length > 0)
        {
            byte[] r = new byte[1 + consolidationsData.Length];
            r[0] = 0x02;
            consolidationsData.CopyTo(r.AsSpan(1));
            reqs.Add(r);
        }

        return reqs.ToArray();
    }

    private static byte[] EncodeStructuredExecutionRequests(byte[][]? reqs)
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

        int fixedSize = 12;
        int totalVar = depositsData.Length + withdrawalsData.Length + consolidationsData.Length;
        byte[] buf = new byte[fixedSize + totalVar];

        int depositsOff = fixedSize;
        int withdrawalsOff = depositsOff + depositsData.Length;
        int consolidationsOff = withdrawalsOff + withdrawalsData.Length;

        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0, 4), (uint)depositsOff);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4, 4), (uint)withdrawalsOff);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(8, 4), (uint)consolidationsOff);

        depositsData.CopyTo(buf.AsSpan(depositsOff));
        withdrawalsData.CopyTo(buf.AsSpan(withdrawalsOff));
        consolidationsData.CopyTo(buf.AsSpan(consolidationsOff));

        return buf;
    }

    private static byte[] EncodeBlobsBundle(BlobsBundleV1? bundle)
    {
        if (bundle is null) return [];

        byte[] commitmentsData = EncodeFixedSizeList(bundle.Commitments);
        byte[] proofsData = EncodeFixedSizeList(bundle.Proofs);
        byte[] blobsData = EncodeFixedSizeList(bundle.Blobs);

        int totalVar = commitmentsData.Length + proofsData.Length + blobsData.Length;
        byte[] buf = new byte[BlobsBundleFixedSize + totalVar];

        int commitmentsOffset = BlobsBundleFixedSize;
        int proofsOffset = commitmentsOffset + commitmentsData.Length;
        int blobsOffset = proofsOffset + proofsData.Length;

        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0, 4), (uint)commitmentsOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4, 4), (uint)proofsOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(8, 4), (uint)blobsOffset);

        commitmentsData.CopyTo(buf.AsSpan(commitmentsOffset));
        proofsData.CopyTo(buf.AsSpan(proofsOffset));
        blobsData.CopyTo(buf.AsSpan(blobsOffset));

        return buf;
    }

    private static byte[] EncodeFixedSizeList(byte[][] items)
    {
        if (items is null || items.Length == 0) return [];

        int totalLen = 0;
        foreach (byte[] item in items)
            totalLen += item.Length;

        byte[] buf = new byte[totalLen];
        int pos = 0;
        foreach (byte[] item in items)
        {
            item.CopyTo(buf.AsSpan(pos));
            pos += item.Length;
        }

        return buf;
    }

    #endregion
}

/// <summary>
/// Exception thrown when SSZ decoding fails.
/// </summary>
public sealed class SszDecodingException : Exception
{
    public SszDecodingException(string message) : base(message) { }
}
