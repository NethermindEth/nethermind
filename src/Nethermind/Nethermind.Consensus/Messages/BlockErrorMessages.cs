// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Consensus.Messages;
public static class BlockErrorMessages
{
    public static string ExceededUncleLimit(int maxUncleCount) =>
        $"ExceededUncleLimit: Cannot have more than {maxUncleCount}.";

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
        "MissingBlobGasUsed: Must be set in header.";

    public const string MissingExcessBlobGas =
        "MissingExcessBlobGas: Must be set in header.";

    public static string InvalidTxInBlock(int i) =>
        $"InvalidTxInBlock: Tx at index {i} in body.";

    public const string HeaderGasUsedMismatch =
        "HeaderGasUsedMismatch: Gas used in header does not match calculated.";

    //Block's blob gas used in header is above the limit.
    public static readonly string BlobGasUsedAboveBlockLimit =
        $"BlockBlobGasExceeded: A block cannot have more than {Eip4844Constants.MaxBlobGasPerBlock} blob gas.";

    //Block's excess blob gas in header is incorrect.
    public const string IncorrectExcessBlobGas =
        "HeaderExcessBlobGasMismatch: Excess blob gas in header does not match calculated.";

    public const string HeaderBlobGasMismatch =
        "HeaderBlobGasMismatch: Blob gas in header does not match calculated.";

    public const string InvalidTimestamp =
        "InvalidTimestamp: Timestamp in header cannot be lower than ancestor.";

    public const string NegativeBlockNumber =
        "NegativeBlockNumber: Block number cannot be negative.";

    public const string NegativeGasLimit =
        "NegativeGasLimit: Gas limit cannot be negative.";

    public const string NegativeGasUsed =
        "NegativeGasUsed: Cannot be negative.";
    public static string MissingValidatorExits => "MissingValidatorExits: Exits cannot be null in block when EIP-7002 activated.";
    public static string ValidatorExitsNotEnabled => "ValidatorExitsNotEnabled: Exits must be null in block when EIP-7002 not activated.";
    public static string InvalidValidatorExitsRoot(Hash256? expected, Hash256? actual) =>
        $"InvalidValidatorExitsRoot: expected {expected}, got {actual}";

    public static string MissingDeposits => "MissingDeposits: Deposits cannot be null in block when EIP-6110 activated.";
    public static string DepositsNotEnabled => "DepositsNotEnabled: Deposits must be null in block when EIP-6110 not activated.";
    public static string InvalidDepositsRoot(Hash256? expected, Hash256? actual) => $"InvalidDepositsRoot: Deposits root hash mismatch in block: expected {expected}, got {actual}";
}
