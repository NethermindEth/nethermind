// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Optimism;

/// <summary>
/// The Optimism specific execution payload.
/// </summary>
public class OptimismExecutionPayloadV3 : ExecutionPayloadV3, IExecutionPayloadFactory<OptimismExecutionPayloadV3>
{
    public Hash256? WithdrawalsRoot { get; set; }

    public new static OptimismExecutionPayloadV3 Create(Block block)
    {
        OptimismExecutionPayloadV3 payload = Create<OptimismExecutionPayloadV3>(block);
        payload.WithdrawalsRoot = block.Header.WithdrawalsRoot;
        return payload;
    }

    public static OptimismExecutionPayloadV3 CreateFrom(ExecutionPayloadV3 source, Hash256? withdrawalsRoot)
    {
        return new OptimismExecutionPayloadV3
        {
            BaseFeePerGas = source.BaseFeePerGas,
            BlockHash = source.BlockHash,
            BlockNumber = source.BlockNumber,
            ExtraData = source.ExtraData,
            FeeRecipient = source.FeeRecipient,
            GasLimit = source.GasLimit,
            GasUsed = source.GasUsed,
            LogsBloom = source.LogsBloom,
            ParentHash = source.ParentHash,
            PrevRandao = source.PrevRandao,
            ReceiptsRoot = source.ReceiptsRoot,
            StateRoot = source.StateRoot,
            Timestamp = source.Timestamp,
            Transactions = source.Transactions,
            Withdrawals = source.Withdrawals,
            ParentBeaconBlockRoot = source.ParentBeaconBlockRoot,
            BlobGasUsed = source.BlobGasUsed,
            ExcessBlobGas = source.ExcessBlobGas,
            WithdrawalsRoot = withdrawalsRoot,
        };
    }

    protected override Hash256? BuildWithdrawalsRoot() => WithdrawalsRoot ?? Keccak.EmptyTreeHash;
}
