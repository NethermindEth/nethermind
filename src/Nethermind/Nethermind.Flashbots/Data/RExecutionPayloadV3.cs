// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
    public Withdrawal[]? withdrawals { get; set; }
    public ulong? blob_gas_used { get; set; }
    public ulong? excess_blob_gas { get; set; }

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
            Withdrawals = withdrawals,
            BlobGasUsed = blob_gas_used,
            ExcessBlobGas = excess_blob_gas
        };
    }

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
        Withdrawal[]? withdrawals,
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
