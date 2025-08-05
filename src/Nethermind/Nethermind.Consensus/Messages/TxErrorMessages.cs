// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Crypto;

namespace Nethermind.Consensus.Messages;
public static class TxErrorMessages
{
    public static string InvalidTxType(string name) =>
        $"InvalidTxType: Transaction type in {name} is not supported.";
    public const string IntrinsicGasTooLow =
        "intrinsic gas too low";
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
        "NotAllowedCreateTransaction: To must be set.";

    public const string BlobTxMissingMaxFeePerBlobGas =
        "BlobTxMissingMaxFeePerBlobGas: Must be set.";

    public const string BlobTxMissingBlobVersionedHashes =
        "blob transaction missing blob hashes";

    public static string BlobTxGasLimitExceeded(ulong totalDataGas, ulong maxBlobGas) =>
        $"BlobTxGasLimitExceeded: Transaction's totalDataGas={totalDataGas} exceeded MaxBlobGas per transaction={maxBlobGas}.";

    public const string BlobTxMissingBlobs =
        "blob transaction missing blob hashes";

    public const string MissingBlobVersionedHash =
        "MissingBlobVersionedHash: Must be set.";

    public static readonly string InvalidBlobVersionedHashSize =
        $"InvalidBlobVersionedHashSize: Cannot exceed {KzgPolynomialCommitments.BytesPerBlobVersionedHash}.";

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

    public const string MissingAuthorizationList = "MissingAuthorizationList: Must be set.";

    public const string InvalidAuthoritySignature = "InvalidAuthoritySignature: Invalid signature in authorization list.";

    public const string InvalidBlobCommitmentHash =
        "InvalidBlobCommitmentHash: Commitment hash does not match.";

    public static string TxGasLimitCapExceeded(long gasLimit, long gasLimitCap)
        => $"TxGasLimitCapExceeded: Gas limit {gasLimit} exceeed cap of {gasLimitCap}.";

    public const string InvalidCreateTxData
        = "InvalidCreateTxData: Legacy createTx cannot create Eof code";

    public const string TooManyEofInitcodes
        = $"TooManyEofInitcodes: Eof initcodes count exceeded limit";

    public const string EmptyEofInitcodesField
        = $"EmptyEofInitcodesField: Eof initcodes count must be greater than 0";

    public const string EofContractSizeInvalid
        = "EofContractSizeInvalid: Eof initcode size is invalid (either 0 or too big)";
}
