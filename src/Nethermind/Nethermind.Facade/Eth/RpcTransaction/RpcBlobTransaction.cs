// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Facade.Eth.RpcTransaction;

public class RpcBlobTransaction : RpcEIP1559Transaction
{
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public UInt256? MaxFeePerBlobGas { get; set; }

    // TODO: Each item should be a 32 byte array
    // Currently we don't enforce this (hashes can have any length)
    public byte[][] BlobVersionedHashes { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Always)]
    public override UInt256? GasPrice { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Always)]
    public override UInt256? V { get; set; }

    public RpcBlobTransaction(Transaction transaction, int? txIndex = null, Hash256? blockHash = null, long? blockNumber = null, UInt256? baseFee = null)
        : base(transaction, txIndex, blockHash, blockNumber, baseFee)
    {
        MaxFeePerBlobGas = transaction.MaxFeePerBlobGas ?? 0;
        BlobVersionedHashes = transaction.BlobVersionedHashes ?? [];
    }

    public new class Converter : IFromTransaction<RpcBlobTransaction>
    {
        public RpcBlobTransaction FromTransaction(Transaction tx, TransactionConverterExtraData extraData)
            => new(tx, txIndex: extraData.TxIndex, blockHash: extraData.BlockHash, blockNumber: extraData.BlockNumber, baseFee: extraData.BaseFee);
    }
}
