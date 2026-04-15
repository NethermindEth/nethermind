// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using Nethermind.Abi;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Evm;
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
    public SystemConfig SystemConfigFromL2BlockInfo(ReadOnlySpan<byte> data, ReadOnlySpan<byte> extraData, ulong gasLimit)
    {
        L1BlockInfo l1BlockInfo = L1BlockInfoBuilder.FromL2DepositTxDataAndExtraData(data, extraData);
        byte[] scalar = new byte[32];
        scalar[0] = 1;
        BinaryPrimitives.WriteUInt32BigEndian(scalar.AsSpan(24), l1BlockInfo.BlobBaseFeeScalar);
        BinaryPrimitives.WriteUInt32BigEndian(scalar.AsSpan(28), l1BlockInfo.BaseFeeScalar);
        return new SystemConfig()
        {
            BatcherAddress = l1BlockInfo.BatcherAddress,
            GasLimit = gasLimit,
            Scalar = scalar,
            EIP1559Params = extraData.ToArray()[1..]
        };
    }

    public SystemConfig UpdateSystemConfigFromL1BLockReceipts(SystemConfig systemConfig, ReceiptForRpc[] receipts)
    {
        SystemConfig config = systemConfig;

        foreach (ReceiptForRpc receipt in receipts)
        {
            if (receipt.Status != StatusCode.Success) continue;

            foreach (LogEntryForRpc log in receipt.Logs ?? [])
            {
                if (log.Address == systemConfigProxy && log.Topics.Length > 0 && log.Topics[0] == SystemConfigUpdate.EventABIHash)
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
            AbiSignature signature = new(nameof(SystemConfigUpdate.Batcher), AbiType.UInt64, AbiType.UInt64, AbiType.Address);
            object[] decoded = AbiEncoder.Instance.Decode(AbiEncodingStyle.None, signature, log.Data);

            if ((UInt64)decoded[0] != 32) throw new FormatException("Invalid pointer field");
            if ((UInt64)decoded[1] != 32) throw new FormatException("Invalid length field");
            Address address = (Address)decoded[2];

            systemConfig = systemConfig with
            {
                BatcherAddress = address
            };
        }
        else if (updateType == SystemConfigUpdate.FeeScalars)
        {
            AbiSignature signature = new(nameof(SystemConfigUpdate.FeeScalars), AbiType.UInt64, AbiType.UInt64, AbiType.Bytes32);
            object[] decoded = AbiEncoder.Instance.Decode(AbiEncodingStyle.None, signature, log.Data);

            if ((UInt64)decoded[0] != 32) throw new FormatException("Invalid pointer field");
            if ((UInt64)decoded[1] != 32) throw new FormatException("Invalid length field");
            byte[] data = (byte[])decoded[2];
            systemConfig = data[0] switch
            {
                0 => systemConfig with { Scalar = data },
                1 when data[1..24].IsZero() => systemConfig with { Scalar = data },
                _ => systemConfig // ignore all other cases
            };
        }
        else if (updateType == SystemConfigUpdate.GasLimit)
        {
            AbiSignature signature = new(nameof(SystemConfigUpdate.GasLimit), AbiType.UInt64, AbiType.UInt64, AbiType.UInt64);
            object[] decoded = AbiEncoder.Instance.Decode(AbiEncodingStyle.None, signature, log.Data);

            if ((UInt64)decoded[0] != 32) throw new FormatException("Invalid pointer field");
            if ((UInt64)decoded[1] != 32) throw new FormatException("Invalid length field");
            ulong gasLimit = (UInt64)decoded[2];

            systemConfig = systemConfig with
            {
                GasLimit = gasLimit
            };
        }
        else if (updateType == SystemConfigUpdate.EIP1559Params)
        {
            AbiSignature signature = new(nameof(SystemConfigUpdate.EIP1559Params), AbiType.UInt64, AbiType.UInt64, AbiType.Bytes32);
            object[] decoded = AbiEncoder.Instance.Decode(AbiEncodingStyle.None, signature, log.Data);

            if ((UInt64)decoded[0] != 32) throw new FormatException("Invalid pointer field");
            if ((UInt64)decoded[1] != 32) throw new FormatException("Invalid length field");
            byte[] eip1559Params = (byte[])decoded[2];

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
