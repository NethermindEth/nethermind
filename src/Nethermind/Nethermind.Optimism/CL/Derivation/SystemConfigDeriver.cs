// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using Nethermind.Abi;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.JsonRpc.Data;

namespace Nethermind.Optimism.CL.Derivation;

public static class SystemConfigUpdate
{
    public static readonly string EventABI = "ConfigUpdate(uint256,uint8,bytes)";
    public static readonly Hash256 EventABIHash = Keccak.Compute(EventABI);
    public static readonly Hash256 EventVersion0 = Hash256.Zero;

    public static readonly Hash256 Batcher = new([.. new byte[31], 0]);
    public static readonly Hash256 FeeScalars = new([.. new byte[31], 1]);
    public static readonly Hash256 GasLimit = new([.. new byte[31], 2]);
    public static readonly Hash256 UnsafeBlockSigner = new([.. new byte[31], 3]);
    public static readonly Hash256 EIP1559Params = new([.. new byte[31], 4]);
}

public class SystemConfigDeriver(
    Address systemConfigProxy
) : ISystemConfigDeriver
{
    private static readonly AbiSignature<(ulong, ulong, Address)> BatcherData = new AbiSignature().With(AbiType.UInt64, AbiType.UInt64, AbiType.Address);
    private static readonly AbiSignature<(ulong, ulong, byte[])> Bytes32Data = new AbiSignature().With(AbiType.UInt64, AbiType.UInt64, AbiType.BytesM(32));
    private static readonly AbiSignature<(ulong, ulong, ulong)> UInt64Data = new AbiSignature().With(AbiType.UInt64, AbiType.UInt64, AbiType.UInt64);

    public SystemConfig SystemConfigFromL2BlockInfo(ReadOnlySpan<byte> data, ReadOnlySpan<byte> extraData, ulong gasLimit)
    {
        L1BlockInfo l1BlockInfo = L1BlockInfoBuilder.FromL2DepositTxDataAndExtraData(data, extraData);
        byte[] scalar = new byte[32];
        scalar[0] = 1;
        BinaryPrimitives.WriteUInt32BigEndian(scalar.AsSpan(24), l1BlockInfo.BlobBaseFeeScalar);
        BinaryPrimitives.WriteUInt32BigEndian(scalar.AsSpan(28), l1BlockInfo.BaseFeeScalar);
        (byte[] eip1559Params, ulong? minBaseFee) = EIP1559ParametersExtensions.SplitHeaderExtraData(extraData);
        return new SystemConfig()
        {
            BatcherAddress = l1BlockInfo.BatcherAddress,
            GasLimit = gasLimit,
            Scalar = scalar,
            EIP1559Params = eip1559Params,
            MinBaseFee = minBaseFee
        };
    }

    public SystemConfig UpdateSystemConfigFromL1BLockReceipts(SystemConfig systemConfig, ReceiptForRpc[] receipts)
    {
        SystemConfig config = systemConfig;

        foreach (ReceiptForRpc receipt in receipts)
        {
            foreach (LogEntryForRpc log in CommittedLogs.Of(receipt))
            {
                if (log.Address == systemConfigProxy && log.Topics is { Length: > 0 } topics && topics[0] == SystemConfigUpdate.EventABIHash)
                {
                    config = UpdateSystemConfigFromLogEvent(config, log);
                }
            }
        }

        return config;
    }

    private SystemConfig UpdateSystemConfigFromLogEvent(SystemConfig systemConfig, LogEntryForRpc log)
    {
        if (log.Topics.Length != 3) throw new ArgumentException($"Expected 3 event topics (event identity, indexed version, indexed updateType), got {log.Topics.Length}");
        if (log.Topics[0] != SystemConfigUpdate.EventABIHash) throw new ArgumentException($"Invalid {nameof(SystemConfig)} update event: {log.Topics[0]}, expected {SystemConfigUpdate.EventABIHash}");
        if (log.Topics[1] != SystemConfigUpdate.EventVersion0) throw new ArgumentException($"Unrecognized {nameof(SystemConfig)} update event version: {log.Topics[1]}");

        Hash256 updateType = log.Topics[2];

        if (updateType == SystemConfigUpdate.Batcher)
        {
            (ulong pointer, ulong length, Address address) = AbiCodec.Decode(BatcherData, log.Data);

            if (pointer != 32) throw new FormatException("Invalid pointer field");
            if (length != 32) throw new FormatException("Invalid length field");

            systemConfig = systemConfig with
            {
                BatcherAddress = address
            };
        }
        else if (updateType == SystemConfigUpdate.FeeScalars)
        {
            (ulong pointer, ulong length, byte[] data) = AbiCodec.Decode(Bytes32Data, log.Data);

            if (pointer != 32) throw new FormatException("Invalid pointer field");
            if (length != 32) throw new FormatException("Invalid length field");
            systemConfig = data[0] switch
            {
                0 => systemConfig with { Scalar = data },
                1 when data[1..24].IsZero() => systemConfig with { Scalar = data },
                _ => systemConfig // ignore all other cases
            };
        }
        else if (updateType == SystemConfigUpdate.GasLimit)
        {
            (ulong pointer, ulong length, ulong gasLimit) = AbiCodec.Decode(UInt64Data, log.Data);

            if (pointer != 32) throw new FormatException("Invalid pointer field");
            if (length != 32) throw new FormatException("Invalid length field");

            systemConfig = systemConfig with
            {
                GasLimit = gasLimit
            };
        }
        else if (updateType == SystemConfigUpdate.EIP1559Params)
        {
            (ulong pointer, ulong length, byte[] eip1559Params) = AbiCodec.Decode(Bytes32Data, log.Data);

            if (pointer != 32) throw new FormatException("Invalid pointer field");
            if (length != 32) throw new FormatException("Invalid length field");

            systemConfig = systemConfig with
            {
                EIP1559Params = eip1559Params
            };
        }
        else if (updateType == SystemConfigUpdate.UnsafeBlockSigner)
        {
            // Ignored in derivation. This configurable applies to runtime configuration outside of the derivation.
            return systemConfig;
        }
        else
        {
            throw new FormatException($"Unknown system config update type: {updateType}");
        }

        return systemConfig;
    }
}
