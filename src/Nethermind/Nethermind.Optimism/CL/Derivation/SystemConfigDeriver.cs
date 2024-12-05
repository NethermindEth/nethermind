// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Abi;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Evm;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Serialization.Rlp;

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
    RollupConfig rollupConfig,
    IReceiptFinder receiptFinder,
    IOptimismSpecHelper specHelper
) : ISystemConfigDeriver
{
    public SystemConfig SystemConfigFromL2Payload(ExecutionPayload l2Payload)
    {
        if (l2Payload.Transactions.Length == 0)
        {
            throw new ArgumentException("No txs in payload");
        }
        Transaction depositTx = TxDecoder.Instance.Decode(l2Payload.Transactions[0]);
        if (depositTx.Type != TxType.DepositTx)
        {
            throw new ArgumentException("First tx is not deposit tx");
        }

        // TODO: all SystemConfig parameters should be encoded in tx.Data();
        throw new System.NotImplementedException();
    }

    public SystemConfig UpdateSystemConfigFromL1BLock(SystemConfig systemConfig, BlockHeader l1Block)
    {
        var config = systemConfig;
        var blockHash = l1Block.Hash ?? throw new ArgumentNullException(nameof(l1Block));
        var receipts = receiptFinder.Get(blockHash);

        foreach (var receipt in receipts)
        {
            if (receipt.StatusCode != StatusCode.Success) continue;

            foreach (var log in receipt.Logs ?? [])
            {
                if (log.Address == rollupConfig.L1SystemConfigAddress && log.Topics.Length > 0 && log.Topics[0] == SystemConfigUpdate.EventABIHash)
                {
                    config = UpdateSystemConfigFromLogEvent(config, l1Block, log);
                }
            }
        }

        return config;
    }

    private SystemConfig UpdateSystemConfigFromLogEvent(SystemConfig systemConfig, BlockHeader header, LogEntry log)
    {
        if (log.Topics.Length != 3) throw new ArgumentException($"Expected 3 event topics (event identity, indexed version, indexed updateType), got {log.Topics.Length}");
        if (log.Topics[0] != SystemConfigUpdate.EventABIHash) throw new ArgumentException($"Invalid {nameof(SystemConfig)} update event: {log.Topics[0]}, expected {SystemConfigUpdate.EventABIHash}");
        if (log.Topics[1] != SystemConfigUpdate.EventVersion0) throw new ArgumentException($"Unrecognized {nameof(SystemConfig)} update event version: {log.Topics[1]}");

        var updateType = log.Topics[2];

        if (updateType == SystemConfigUpdate.Batcher)
        {
            var signature = new AbiSignature(nameof(SystemConfigUpdate.Batcher), AbiType.UInt64, AbiType.UInt64, AbiType.Address);
            object[] decoded = AbiEncoder.Instance.Decode(AbiEncodingStyle.None, signature, log.Data);

            if ((UInt64)decoded[0] != 32) throw new FormatException("Invalid pointer field");
            if ((UInt64)decoded[1] != 32) throw new FormatException("Invalid length field");
            var address = (Address)decoded[2];

            systemConfig = systemConfig with
            {
                BatcherAddress = address
            };
        }
        else if (updateType == SystemConfigUpdate.FeeScalars)
        {
            var signature = new AbiSignature(nameof(SystemConfigUpdate.FeeScalars), AbiType.UInt64, AbiType.UInt64, AbiType.Bytes32, AbiType.Bytes32);
            object[] decoded = AbiEncoder.Instance.Decode(AbiEncodingStyle.None, signature, log.Data);

            if ((UInt64)decoded[0] != 32) throw new FormatException("Invalid pointer field");
            if ((UInt64)decoded[1] != 64) throw new FormatException("Invalid length field");
            var overhead = (byte[])decoded[2];
            var scalar = (byte[])decoded[3];

            if (specHelper.IsEcotone(header))
            {
                if (!ValidEcotoneL1SystemConfigScalar(scalar))
                {
                    // ignore invalid scalars, retain the old system-config scalar
                    return systemConfig;
                }

                systemConfig = systemConfig with
                {
                    // retain the scalar data in encoded form
                    Scalar = scalar,
                    // zero out the overhead, it will not affect the state-transition after Ecotone
                    Overhead = new byte[32]
                };
            }
            else
            {
                systemConfig = systemConfig with
                {
                    Overhead = overhead,
                    Scalar = scalar,
                };
            }
        }
        else if (updateType == SystemConfigUpdate.GasLimit)
        {
            var signature = new AbiSignature(nameof(SystemConfigUpdate.GasLimit), AbiType.UInt64, AbiType.UInt64, AbiType.UInt64);
            object[] decoded = AbiEncoder.Instance.Decode(AbiEncodingStyle.None, signature, log.Data);

            if ((UInt64)decoded[0] != 32) throw new FormatException("Invalid pointer field");
            if ((UInt64)decoded[1] != 32) throw new FormatException("Invalid length field");
            var gasLimit = (UInt64)decoded[2];

            systemConfig = systemConfig with
            {
                GasLimit = gasLimit
            };
        }
        else if (updateType == SystemConfigUpdate.EIP1559Params)
        {
            var signature = new AbiSignature(nameof(SystemConfigUpdate.EIP1559Params), AbiType.UInt64, AbiType.UInt64, AbiType.Bytes32);
            object[] decoded = AbiEncoder.Instance.Decode(AbiEncodingStyle.None, signature, log.Data);

            if ((UInt64)decoded[0] != 32) throw new FormatException("Invalid pointer field");
            if ((UInt64)decoded[1] != 32) throw new FormatException("Invalid length field");
            var eip1559Params = (byte[])decoded[2];

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

    private static bool ValidEcotoneL1SystemConfigScalar(ReadOnlySpan<byte> scalar)
    {
        const byte L1ScalarBedrock = 0;
        const byte L1ScalarEcotone = 1;

        var versionByte = scalar[0];
        return versionByte switch
        {
            L1ScalarBedrock => scalar[1..28].IsZero(),
            L1ScalarEcotone => scalar[1..24].IsZero(),
            // ignore the event if it's an unknown scalar format
            _ => false,
        };
    }
}
