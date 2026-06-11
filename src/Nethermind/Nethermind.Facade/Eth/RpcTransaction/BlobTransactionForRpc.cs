// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Specs;
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
    public byte[]?[]? BlobVersionedHashes { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public byte[][]? Blobs { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public byte[][]? Commitments { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public byte[][]? Proofs { get; set; }

    [JsonConstructor]
    public BlobTransactionForRpc() { }

    public BlobTransactionForRpc(Transaction transaction, in TransactionForRpcContext extraData)
        : base(transaction, extraData)
    {
        MaxFeePerBlobGas = transaction.MaxFeePerBlobGas ?? 0;
        BlobVersionedHashes = transaction.BlobVersionedHashes ?? [];

        if (transaction.NetworkWrapper is ShardBlobNetworkWrapper wrapper)
        {
            Blobs = wrapper.Blobs;
            Commitments = wrapper.Commitments;
            Proofs = wrapper.Proofs;
        }
    }

    public override Result<Transaction> ToTransaction(bool validateUserInput = false, ulong? gasCap = null, IReleaseSpec? spec = null)
    {
        byte[]?[]? blobVersionedHashes = BlobVersionedHashes;
        if (blobVersionedHashes is null || blobVersionedHashes.Length == 0)
            return RpcTransactionErrors.AtLeastOneBlobInBlobTransaction;

        byte[][] validatedBlobVersionedHashes = new byte[blobVersionedHashes.Length][];
        for (int i = 0; i < blobVersionedHashes.Length; i++)
        {
            byte[]? hash = blobVersionedHashes[i];
            if (hash is null || hash.Length != Eip4844Constants.BytesPerBlobVersionedHash)
                return RpcTransactionErrors.InvalidBlobVersionedHashSize;

            if (hash[0] != KzgPolynomialCommitments.KzgBlobHashVersionV1)
                return RpcTransactionErrors.InvalidBlobVersionedHashVersion;

            validatedBlobVersionedHashes[i] = hash;
        }

        if (To is null)
            return RpcTransactionErrors.MissingToInBlobTx;

        if (validateUserInput && MaxFeePerBlobGas?.IsZero == true)
            return RpcTransactionErrors.ZeroMaxFeePerBlobGas;

        if (validateUserInput && spec?.IsEip4844Enabled == true)
        {
            ValidationResult blobCountValidation = BlobFieldsTxValidator.ValidateBlobGasLimits(validatedBlobVersionedHashes.Length, spec);
            if (!blobCountValidation)
                return blobCountValidation.Error!;
        }

        Result<Transaction> baseResult = base.ToTransaction(validateUserInput, gasCap, spec);
        if (!baseResult) return baseResult;

        Transaction tx = baseResult.Data!;

        if (tx.SupportsBlobs)
        {
            tx.MaxFeePerBlobGas = MaxFeePerBlobGas;
            tx.BlobVersionedHashes = validatedBlobVersionedHashes;
        }

        return tx;
    }

    public new static BlobTransactionForRpc FromTransaction(Transaction tx, in TransactionForRpcContext extraData)
        => new(tx, extraData);

    /// <summary>
    /// Validates the blob sidecar fields and attaches a <see cref="ShardBlobNetworkWrapper"/>
    /// to the given <see cref="Transaction"/>. Returns an error string on failure, or
    /// <c>null</c> on success.
    /// </summary>
    public string? TryAttachSidecar(Transaction tx, ProofVersion version)
    {
        string? fieldError = this switch
        {
            { Blobs: null or { Length: 0 } } => "blob transaction requires non-empty blobs",
            { Commitments: null } => "commitments must be provided alongside blobs",
            { Proofs: null } => "proofs must be provided alongside blobs",
            _ => null
        };
        if (fieldError is not null) return fieldError;

        ShardBlobNetworkWrapper wrapper = new(Blobs!, Commitments!, Proofs!, version);
        IBlobProofsManager manager = IBlobProofsManager.For(version);
        if (!manager.ValidateLengths(wrapper))
            return "blob sidecar lengths invalid (blobs/commitments/proofs counts or individual byte sizes)";
        if (tx.BlobVersionedHashes is not null)
        {
            byte[]?[] blobVersionedHashes = tx.BlobVersionedHashes;
            byte[][] validatedBlobVersionedHashes = new byte[blobVersionedHashes.Length][];
            for (int i = 0; i < blobVersionedHashes.Length; i++)
            {
                if (blobVersionedHashes[i] is not { } blobVersionedHash)
                    return "blobVersionedHashes contains null";

                validatedBlobVersionedHashes[i] = blobVersionedHash;
            }

            if (!manager.ValidateHashes(wrapper, validatedBlobVersionedHashes))
                return "blob commitments do not match the supplied blobVersionedHashes";
        }

        tx.NetworkWrapper = wrapper;
        return null;
    }
}
