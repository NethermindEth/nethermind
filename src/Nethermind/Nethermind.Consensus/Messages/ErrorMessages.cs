// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Crypto;

namespace Nethermind.Consensus.Messages;
public static class ErrorMessages
{
    public static string ExceededUncleLimit(int maxUncleCount) => $"ExceededUncleLimit: Cannot have more than {maxUncleCount}.";

    public const string InsufficientMaxFeePerBlobGas =
                    "InsufficientMaxFeePerBlobGas: Not enough to cover blob gas fee.";

    public const string InvalidLogsBloom =
                "InvalidLogsBloom: Logs bloom in header does not match.";

    public static string InvalidTxRoot(Core.Crypto.Hash256 expected, Core.Crypto.Hash256 actual) =>
            $"InvalidTxRoot: Expected {expected}, got {actual}";

    public const string InvalidUncle =
            "InvalidUncle: Uncle could not be validated.";

    public const string InvalidUnclesHash =
            "InvalidUnclesHash: Uncle header hash does not match.";

    public static string InvalidWithdrawalsRoot(Core.Crypto.Hash256 expected, Core.Crypto.Hash256 actual) =>
        $"InvalidWithdrawalsRoot: expected {expected}, got {actual}";


    public const string MissingWithdrawals =
        "MissingWithdrawals: Block body is missing withdrawals.";

    public const string WithdrawalsNotEnabled =
        "WithdrawalsNotEnabled: Block body cannot have withdrawals.";

    public const string InvalidReceiptsRoot =
        "InvalidReceiptsRoot: Receipts root in header does not match.";

    public const string InvalidStateRoot =
            "InvalidStateRoot: State root in header does not match.";

    public const string InvalidParentBeaconBlockRoot =
        "InvalidParentBeaconBlockRoot: Beacon block root in header does not match.";

    public const string BlobGasPriceOverflow =
                    "BlobGasPriceOverflow: Overflow in excess blob gas.";

    public const string InvalidHeaderHash =
                    "InvalidHeaderHash: Header hash does not match.";

    public const string InvalidExtraData =
                "InvalidExtraData: Extra data in header is not valid.";

    public const string InvalidGenesisBlock =
                        "InvalidGenesisBlock: Genesis block could not be validated.";

    public const string InvalidAncestor =
                "InvalidAncestor: No valid ancestors could be found.";

    public const string InvalidTotalDifficulty =
                "InvalidTotalDifficulty: Could not be validated.";

    public const string InvalidSealParameters =
                "InvalidSealParameters: Could not be validated.";

    public const string ExceededGasLimit =
                "ExceededGasLimit: Gas used exceeds gas limit.";

    public const string InvalidGasLimit =
                "InvalidGasLimit: Gas limit is not correct.";

    public const string InvalidBlockNumber =
                "InvalidBlockNumber: Block number does not match the parent.";

    public const string InvalidBaseFeePerGas =
                    "InvalidBaseFeePerGas: Does not match calculated.";

    public const string NotAllowedBlobGasUsed =
                    "NotAllowedBlobGasUsed: Cannot be set.";

    public const string NotAllowedExcessBlobGas =
                    "NotAllowedExcessBlobGas: Cannot be set.";

    public const string MissingBlobGasUsed =
                "MissingBlobGasUsed: Must be set in blob transaction.";

    public const string MissingExcessBlobGas =
                "MissingExcessBlobGas: Must be set in blob transaction.";

    public const string IntrinsicGasTooLow =
                "IntrinsicGasTooLow: Gas limit is too low.";

    public const string InvalidTxSignature =
                "InvalidTxSignature: Signature is invalid.";

    public const string InvalidMaxPriorityFeePerGas =
                "InvalidMaxPriorityFeePerGas: Cannot be higher than maxFeePerGas.";

    public const string ContractSizeTooBig =
                "ContractSizeTooBig: Max initcode size exceeded.";

    public const string NotAllowedMaxFeePerBlobGas =
                    "NotAllowedMaxFeePerBlobGas: Cannot be set.";

    public const string NotAllowedBlobVersionedHashes =
                    "NotAllowedBlobVersionedHashes: Cannot be set.";

    public const string InvalidTransaction =
                    $"InvalidTransaction: Cannot be {nameof(ShardBlobNetworkWrapper)}.";

    public const string TxMissingTo =
        "TxMissingTo: Must be set.";

    public const string BlobTxMissingMaxFeePerBlobGas =
                "BlobTxMissingMaxFeePerBlobGas: Must be set.";

    public const string BlobTxMissingBlobVersionedHashes =
                "BlobTxMissingBlobVersionedHashes: Must be set.";

    public static string BlobTxGasLimitExceeded =>
                $"BlobTxGasLimitExceeded: Transaction exceeded {Eip4844Constants.MaxBlobGasPerTransaction}.";

    public const string BlobTxMissingBlobs =
                "BlobTxMissingBlobs: Blob transaction must have blobs.";

    public const string MissingBlobVersionedHash =
                    "MissingBlobVersionedHash: Must be set.";

    public static string InvalidBlobVersionedHashSize =>
                    $"InvalidBlobVersionedHashSize: Cannot exceed {KzgPolynomialCommitments.BytesPerBlobVersionedHash}.";

    public const string InvalidBlobVersionedHashVersion =
                    "InvalidBlobVersionedHashVersion: Blob version not supported.";

    public static string ExceededBlobSize =>
                        $"ExceededBlobSize: Cannot be more than {Ckzg.Ckzg.BytesPerBlob}.";

    public static string ExceededBlobCommitmentSize =>
                        $"ExceededBlobCommitmentSize: Cannot be more than {Ckzg.Ckzg.BytesPerCommitment}.";

    public static string InvalidBlobProofSize =>
                        $"InvalidBlobProofSize: Cannot be more than {Ckzg.Ckzg.BytesPerProof}.";

    public const string InvalidBlobCommitmentHash =
                        "InvalidBlobCommitmentHash: Commitment hash does not match.";

    public const string InvalidBlobProof =
                    "InvalidBlobProof: Proof does not match.";

    public static string InvalidTxInBlock(int i) =>
                $"InvalidTxInBlock: Tx at index {i} in body.";

    public static string InvalidTxType(string name) =>
        $"InvalidTxType: Transaction type in {name} is not supported.";

    public static string InvalidTxChainId(ulong expected, ulong? actual) =>
                $"InvalidTxChainId: Expected {expected}, got {actual}.";
}
