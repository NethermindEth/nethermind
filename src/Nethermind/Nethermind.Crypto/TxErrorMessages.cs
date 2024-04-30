// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Crypto;
public static class TxErrorMessages
{
    public static string InvalidTxType(string name) =>
        $"InvalidTxType: transaction type in {name} is not supported.";

    public const string IntrinsicGasTooLow =
        "IntrinsicGasTooLow: gas limit is below intrinsic gas.";

    public const string InvalidTxSignature =
        "InvalidTxSignature: signature is invalid.";
    public static string InvalidTxChainId(ulong expected, ulong? actual) =>
        $"InvalidTxChainId: expected {expected}, got {actual}.";

    public const string InvalidMaxPriorityFeePerGas =
        "InvalidMaxPriorityFeePerGas: cannot be higher than maxFeePerGas.";

    public const string ContractSizeTooBig =
        "ContractSizeTooBig: max initcode size exceeded.";

    public const string NotAllowedMaxFeePerBlobGas =
        "NotAllowedMaxFeePerBlobGas: cannot be set.";

    public const string NotAllowedBlobVersionedHashes =
        "NotAllowedBlobVersionedHashes: cannot be set.";

    public const string InvalidTransaction =
        $"InvalidTransaction: cannot be {nameof(ShardBlobNetworkWrapper)}.";

    public const string TxMissingTo =
        "TxMissingTo: must be set.";

    public const string BlobTxMissingMaxFeePerBlobGas =
        "BlobTxMissingMaxFeePerBlobGas: must be set.";

    public const string BlobTxMissingBlobVersionedHashes =
        "BlobTxMissingBlobVersionedHashes: must be set.";

    public static readonly string BlobTxGasLimitExceeded =
        $"BlobTxGasLimitExceeded: transaction exceeded {Eip4844Constants.MaxBlobGasPerTransaction}.";

    public const string BlobTxMissingBlobs =
        "BlobTxMissingBlobs: blob transaction must have blobs.";

    public const string MissingBlobVersionedHash =
        "MissingBlobVersionedHash: must be set.";

    public static readonly string InvalidBlobVersionedHashSize =
        $"InvalidBlobVersionedHashSize: cannot exceed {KzgPolynomialCommitments.BytesPerBlobVersionedHash}.";

    public const string InvalidBlobVersionedHashVersion =
        "InvalidBlobVersionedHashVersion: blob version not supported.";

    public static readonly string ExceededBlobSize =
        $"ExceededBlobSize: cannot be more than {Ckzg.Ckzg.BytesPerBlob}.";

    public static readonly string ExceededBlobCommitmentSize =
        $"ExceededBlobCommitmentSize: cannot be more than {Ckzg.Ckzg.BytesPerCommitment}.";

    public static readonly string InvalidBlobProofSize =
        $"InvalidBlobProofSize: cannot be more than {Ckzg.Ckzg.BytesPerProof}.";

    public const string InvalidBlobCommitmentHash =
        "InvalidBlobCommitmentHash: commitment hash does not match.";

    public const string InvalidBlobProof =
        "InvalidBlobProof: proof does not match.";

    public const string InvalidBlobData
        = "InvalidTxBlobData: number of blobs, hashes, commitments and proofs must match.";

    // TxProcessor

    public const string SenderNotSpecified
        = "SenderNotSpecified: sender is not specified.";

    public const string NonceOverflow
        = "NonceOverflow: nonce value exceeded max limits.";

    public const string CreateTxSizeExceedsMaxInitCodeSize
        = "CreateTxSizeExceedsMaxInitCodeSize: transaction size over max init code size from EIP-3860.";

    public const string BlockGasLimitExceeded
        = "BlockGasLimitExceeded: block gas limit exceeded.";

    public const string SenderIsContract
        = "SenderIsContract: sender address has deployed code.";

    public const string GasPriceTooLow
        = "GasPriceTooLow: max fee per gas less than block base fee.";

    public const string InsufficientFundsForTxValue
        = "InsufficientFundsForTxValue: sender has insufficient funds for transaction value.";

    public const string InsufficientFundsForTxGas
        = "InsufficientFundsForTxGas: sender has insufficient funds for transaction gas.";

    public const string InsufficientFundsForBlobGas
        = "InsufficientFundsForBlobGas: sender has insufficient funds for blob gas.";

    public static string NonceTooLow(in UInt256 currentNonce, in UInt256 txNonce)
        => $"NonceTooLow: nonce too low. Current nonce: {currentNonce}, nonce of tx: {txNonce}.";

    public static string NonceTooHigh(in UInt256 currentNonce, in UInt256 txNonce)
        => $"NonceTooLow: nonce too high. Current nonce: {currentNonce}, nonce of tx: {txNonce}.";

    public static string FutureNonce(in ulong expectedNonce, in UInt256 txNonce)
        => $"FutureNonce: nonce too high. Expected nonce: {expectedNonce}, nonce of tx: {txNonce}.";

    public const string AlreadyKnown
        = "AlreadyKnown: transaction is already known.";
}
}
