// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Int256;

namespace Nethermind.Facade.Eth.RpcTransaction;

public class BlobTransactionForRpc : EIP1559TransactionForRpc, IFromTransaction<BlobTransactionForRpc>
{
    public new static TxType TxType => TxType.Blob;

    public override TxType? Type => TxType;

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public UInt256? MaxFeePerBlobGas { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    [JsonDiscriminator]
    public byte[][]? BlobVersionedHashes { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public byte[][]? Blobs { get; set; }

    [JsonConstructor]
    public BlobTransactionForRpc() { }

    public BlobTransactionForRpc(Transaction transaction, int? txIndex = null, Hash256? blockHash = null, long? blockNumber = null, UInt256? baseFee = null, ulong? chainId = null)
        : base(transaction, txIndex, blockHash, blockNumber, baseFee, chainId)
    {
        MaxFeePerBlobGas = transaction.MaxFeePerBlobGas ?? 0;
        BlobVersionedHashes = transaction.BlobVersionedHashes ?? [];
    }

    public override Result<Transaction> ToTransaction(bool validateUserInput = true)
    {
        if (BlobVersionedHashes is null || BlobVersionedHashes.Length == 0)
            return RpcTransactionErrors.AtLeastOneBlobInBlobTransaction;

        foreach (byte[]? hash in BlobVersionedHashes)
        {
            if (hash is null || hash.Length != Eip4844Constants.BytesPerBlobVersionedHash)
                return RpcTransactionErrors.InvalidBlobVersionedHashSize;

            if (hash[0] != KzgPolynomialCommitments.KzgBlobHashVersionV1)
                return RpcTransactionErrors.InvalidBlobVersionedHashVersion;
        }

        if (To is null)
            return RpcTransactionErrors.MissingToInBlobTx;

        if (validateUserInput)
        {
            if (MaxFeePerBlobGas?.IsZero == true)
                return RpcTransactionErrors.ZeroMaxFeePerBlobGas;
        }

        Result<Transaction> baseResult = base.ToTransaction(validateUserInput);
        if (!baseResult) return baseResult;

        Transaction tx = baseResult.Data;
        tx.MaxFeePerBlobGas = MaxFeePerBlobGas;
        tx.BlobVersionedHashes = BlobVersionedHashes;

        return tx;
    }

    public new static BlobTransactionForRpc FromTransaction(Transaction tx, TransactionConverterExtraData extraData)
        => new(tx, txIndex: extraData.TxIndex, blockHash: extraData.BlockHash, blockNumber: extraData.BlockNumber, baseFee: extraData.BaseFee, chainId: extraData.ChainId);
}
