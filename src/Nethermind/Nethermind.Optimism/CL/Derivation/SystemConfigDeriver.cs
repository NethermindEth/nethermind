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
using Nethermind.Optimism.CL.Decoders;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Optimism.CL;

public static class SystemConfigUpdate
{
    public static readonly string EventABI = "ConfigUpdate(uint256,uint8,bytes)";
    public static readonly Hash256 EventABIHash = Keccak.Compute(EventABI);
    public static readonly Hash256 EventVersion0 = Hash256.Zero;

    public static readonly Hash256 Batcher;
    public static readonly Hash256 FeeScalars;
    public static readonly Hash256 GasLimit;
    public static readonly Hash256 UnsafeBlockSigner;
    public static readonly Hash256 EIP1559Params;

    // TODO: There has to be a more elegant way of doing this
    static SystemConfigUpdate()
    {
        var updateBatcher = new byte[32];
        updateBatcher[31] = 0;
        Batcher = new Hash256(updateBatcher);

        var updateFeeScalars = new byte[32];
        updateFeeScalars[31] = 1;
        FeeScalars = new Hash256(updateFeeScalars);

        var updateGasLimit = new byte[32];
        updateGasLimit[31] = 2;
        GasLimit = new Hash256(updateGasLimit);

        var updateUnsafeBlockSigner = new byte[32];
        updateUnsafeBlockSigner[31] = 3;
        UnsafeBlockSigner = new Hash256(updateUnsafeBlockSigner);

        var updateEIP1559Params = new byte[32];
        updateEIP1559Params[31] = 4;
        EIP1559Params = new Hash256(updateEIP1559Params);
    }
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
        var blockHash = l1Block.Hash ?? throw new InvalidOperationException("Block hash is null");
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
        int offset = 0;

        // TODO: Remove
        ReadOnlySpan<byte> data = log.Data;

        if (updateType == SystemConfigUpdate.Batcher)
        {
            UInt64 pointer;
            (pointer, offset) = ((UInt64, int))AbiType.UInt64.Decode(log.Data, offset, packed: false);
            if (pointer != 32) throw new FormatException("Invalid pointer field");

            UInt64 length;
            (length, offset) = ((UInt64, int))AbiType.UInt64.Decode(log.Data, offset, packed: false);
            if (length != 32) throw new FormatException("Invalid length field");

            Address address;
            (address, offset) = ((Address, int))AbiType.Address.Decode(log.Data, offset, packed: false);

            systemConfig = systemConfig with
            {
                BatcherAddress = address
            };
        }
        else if (updateType == SystemConfigUpdate.FeeScalars)
        {
            var pointer = SolidityAbiDecoder.ReadUInt64(data.TakeAndMove(32));
            if (pointer != 32) throw new FormatException("Invalid pointer field");

            var length = SolidityAbiDecoder.ReadUInt64(data.TakeAndMove(32));
            if (length != 64) throw new FormatException("Invalid length field");

            var overhead = data.TakeAndMove(32).ToArray();
            var scalar = data.TakeAndMove(32).ToArray();

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
            var pointer = SolidityAbiDecoder.ReadUInt64(data.TakeAndMove(32));
            if (pointer != 32) throw new FormatException("Invalid pointer field");

            var length = SolidityAbiDecoder.ReadUInt64(data.TakeAndMove(32));
            if (length != 32) throw new FormatException("Invalid length field");

            var gasLimit = SolidityAbiDecoder.ReadUInt64(data.TakeAndMove(32));
            systemConfig = systemConfig with
            {
                GasLimit = gasLimit
            };
        }
        else if (updateType == SystemConfigUpdate.EIP1559Params)
        {
            var pointer = SolidityAbiDecoder.ReadUInt64(data.TakeAndMove(32));
            if (pointer != 32) throw new FormatException("Invalid pointer field");

            var length = SolidityAbiDecoder.ReadUInt64(data.TakeAndMove(32));
            if (length != 32) throw new FormatException("Invalid length field");

            var eip1559Params = data.TakeAndMove(32).ToArray();
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

        if (offset != log.Data.Length && data.Length != 0)
        {
            throw new FormatException("Too many bytes");
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
