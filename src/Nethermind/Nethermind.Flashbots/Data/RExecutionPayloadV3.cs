// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Merge.Plugin.Data;

public class RExecutionPayloadV3
{
    public Hash256 parent_hash { get; set; }
    public Address fee_recipient { get; set; }
    public Hash256 state_root { get; set; }
    public Hash256 receipts_root { get; set; }
    public Bloom logs_bloom { get; set; }
    public Hash256 prev_randao { get; set; }
    public long block_number { get; set; }
    public long gas_limit { get; set; }
    public long gas_used { get; set; }
    public ulong timestamp { get; set; }
    public byte[] extra_data { get; set; }
    public UInt256 base_fee_per_gas { get; set; }
    public Hash256 block_hash { get; set; }
    public byte[][] transactions { get; set; }
    public RWithdrawal[]? withdrawals { get; set; }
    public ulong? blob_gas_used { get; set; }
    public ulong? excess_blob_gas { get; set; }

    public RExecutionPayloadV3(ExecutionPayloadV3 executionPayloadV3)
    {
        parent_hash = executionPayloadV3.ParentHash;
        fee_recipient = executionPayloadV3.FeeRecipient;
        state_root = executionPayloadV3.StateRoot;
        receipts_root = executionPayloadV3.ReceiptsRoot;
        logs_bloom = executionPayloadV3.LogsBloom;
        prev_randao = executionPayloadV3.PrevRandao;
        block_number = executionPayloadV3.BlockNumber;
        gas_limit = executionPayloadV3.GasLimit;
        gas_used = executionPayloadV3.GasUsed;
        timestamp = executionPayloadV3.Timestamp;
        extra_data = executionPayloadV3.ExtraData;
        base_fee_per_gas = executionPayloadV3.BaseFeePerGas;
        block_hash = executionPayloadV3.BlockHash;
        transactions = executionPayloadV3.Transactions;
        withdrawals = executionPayloadV3.Withdrawals?.Select(w => new RWithdrawal(w)).ToArray();
        blob_gas_used = executionPayloadV3.BlobGasUsed;
        excess_blob_gas = executionPayloadV3.ExcessBlobGas;
    }
    public ExecutionPayloadV3 ToExecutionPayloadV3()
    {
        return new ExecutionPayloadV3
        {
            ParentHash = parent_hash,
            FeeRecipient = fee_recipient,
            StateRoot = state_root,
            ReceiptsRoot = receipts_root,
            LogsBloom = logs_bloom,
            PrevRandao = prev_randao,
            BlockNumber = block_number,
            GasLimit = gas_limit,
            GasUsed = gas_used,
            Timestamp = timestamp,
            ExtraData = extra_data,
            BaseFeePerGas = base_fee_per_gas,
            BlockHash = block_hash,
            Transactions = transactions,
            Withdrawals = withdrawals?.Select(w => w.ToWithdrawal()).ToArray(),
            BlobGasUsed = blob_gas_used,
            ExcessBlobGas = excess_blob_gas
        };
    }


    [JsonConstructor]
    public RExecutionPayloadV3(
        Hash256 parent_hash,
        Address fee_recipient,
        Hash256 state_root,
        Hash256 receipts_root,
        Bloom logs_bloom,
        Hash256 prev_randao,
        long block_number,
        long gas_limit,
        long gas_used,
        ulong timestamp,
        byte[] extra_data,
        UInt256 base_fee_per_gas,
        Hash256 block_hash,
        byte[][] transactions,
        RWithdrawal[]? withdrawals,
        ulong? blob_gas_used,
        ulong? excess_blob_gas
    )
    {
        this.parent_hash = parent_hash;
        this.fee_recipient = fee_recipient;
        this.state_root = state_root;
        this.receipts_root = receipts_root;
        this.logs_bloom = logs_bloom;
        this.prev_randao = prev_randao;
        this.block_number = block_number;
        this.gas_limit = gas_limit;
        this.gas_used = gas_used;
        this.timestamp = timestamp;
        this.extra_data = extra_data;
        this.base_fee_per_gas = base_fee_per_gas;
        this.block_hash = block_hash;
        this.transactions = transactions;
        this.withdrawals = withdrawals;
        this.blob_gas_used = blob_gas_used;
        this.excess_blob_gas = excess_blob_gas;
    }
}
