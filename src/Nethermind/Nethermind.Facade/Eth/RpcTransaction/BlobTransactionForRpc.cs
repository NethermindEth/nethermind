// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;
using Nethermind.Core;
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

    public BlobTransactionForRpc(Transaction transaction, in TransactionForRpcContext extraData)
        : base(transaction, extraData)
    {
        MaxFeePerBlobGas = transaction.MaxFeePerBlobGas ?? 0;
        BlobVersionedHashes = transaction.BlobVersionedHashes ?? [];
    }

    public override Result<Transaction> ToTransaction(bool validateUserInput = false)
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

        if (validateUserInput && MaxFeePerBlobGas?.IsZero == true)
            return RpcTransactionErrors.ZeroMaxFeePerBlobGas;

        Result<Transaction> baseResult = base.ToTransaction(validateUserInput);
        if (!baseResult) return baseResult;

        Transaction tx = baseResult.Data;
        tx.MaxFeePerBlobGas = MaxFeePerBlobGas;
        tx.BlobVersionedHashes = BlobVersionedHashes;

        return tx;
    }

    public new static BlobTransactionForRpc FromTransaction(Transaction tx, in TransactionForRpcContext extraData)
        => new(tx, extraData);
}
