// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core.Messages;

public static class TxErrorMessages
{
    public static string InvalidTxType(string name) =>
        $"InvalidTxType: Transaction type in {name} is not supported.";
    public const string IntrinsicGasTooLow =
        "intrinsic gas too low";
    public const string GasBelowFloorDataCost =
        "gas below floor data cost";
    public const string InsufficientFundsForTransfer =
        "insufficient funds for transfer";
    public const string TxMissingTo =
        "blob transaction of type create";

    public const string InvalidTxSignature =
        "InvalidTxSignature: Signature is invalid.";
    public static string InvalidTxChainId(ulong expected, ulong? actual) =>
        $"InvalidTxChainId: Expected {expected}, got {actual}.";

    public const string InvalidMaxPriorityFeePerGas =
        "InvalidMaxPriorityFeePerGas: Cannot be higher than maxFeePerGas.";

    public const string ContractSizeTooBig =
        "max initcode size exceeded";

    public const string NotAllowedMaxFeePerBlobGas =
        "NotAllowedMaxFeePerBlobGas: Cannot be set.";

    public const string NotAllowedBlobVersionedHashes =
        "NotAllowedBlobVersionedHashes: Cannot be set.";

    public const string InvalidTransactionForm =
        $"InvalidTransaction: Cannot be {nameof(ShardBlobNetworkWrapper)}.";

    public const string NotAllowedCreateTransaction =
        "EIP-7702 transaction cannot be used to create contract";

    public const string BlobTxMissingMaxFeePerBlobGas =
        "BlobTxMissingMaxFeePerBlobGas: Must be set.";

    public const string BlobTxMissingBlobVersionedHashes =
        "blob transaction missing blob hashes";

    public static string BlobTxGasLimitExceeded(ulong totalDataGas, ulong maxBlobGas) =>
        $"BlobTxGasLimitExceeded: Transaction's totalDataGas={totalDataGas} exceeded MaxBlobGas per transaction={maxBlobGas}.";

    public static readonly string BlobTxMissingBlobs =
        $"blob transaction must have at least {Eip4844Constants.MinBlobsPerTransaction} blob";

    public const string MissingBlobVersionedHash =
        "MissingBlobVersionedHash: Must be set.";

    public static readonly string InvalidBlobVersionedHashSize =
        $"InvalidBlobVersionedHashSize: Cannot exceed {Eip4844Constants.BytesPerBlobVersionedHash}.";

    public const string InvalidBlobVersionedHashVersion =
        "InvalidBlobVersionedHashVersion: Blob version not supported.";

    public static readonly string InvalidBlobDataSize =
        $"InvalidBlobDataSize: Blob data fields are of incorrect size.";

    public const string InvalidBlobHashes =
        "InvalidBlobProof: Hashes do not match the blobs.";

    public const string InvalidBlobProofs =
        "InvalidBlobProof: Proofs do not match the blobs.";

    public const string InvalidProofVersion =
        "InvalidTxProofVersion: Version of network wrapper is not supported.";



    public const string NotAllowedAuthorizationList = $"NotAllowedAuthorizationList: Only transactions with type {nameof(TxType.SetCode)} can have authorization_list.";

    public const string MissingAuthorizationList = "EIP-7702 transaction with empty auth list";

    public const string InvalidAuthoritySignature = "InvalidAuthoritySignature: Invalid signature in authorization list.";

    public const string InvalidBlobCommitmentHash =
        "InvalidBlobCommitmentHash: Commitment hash does not match.";

    public static string TxGasLimitCapExceeded(ulong gasLimit, ulong gasLimitCap)
        => $"TxGasLimitCapExceeded: Gas limit {gasLimit} exceeded cap of {gasLimitCap}.";

    public static string TxIntrinsicGasExceedsCap(ulong intrinsicRegularGas, ulong intrinsicFloorGas, ulong gasLimitCap)
        => $"{IntrinsicGasTooLow}: Intrinsic gas (regular {intrinsicRegularGas}, floor {intrinsicFloorGas}) exceeded cap of {gasLimitCap}.";

    public const string NonceTooHigh = "NonceTooHigh: Nonce exceeds max nonce";

}
