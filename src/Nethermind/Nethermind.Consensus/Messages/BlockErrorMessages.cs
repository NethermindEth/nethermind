// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Consensus.Messages;
public static class BlockErrorMessages
{
    public static string ExceededUncleLimit(int maxUncleCount) =>
        $"ExceededUncleLimit: Cannot have more than {maxUncleCount}.";

    public const string InsufficientMaxFeePerBlobGas =
        "InsufficientMaxFeePerBlobGas: Not enough to cover blob gas fee.";

    public static string InvalidLogsBloom(Bloom expected, Bloom actual) =>
        $"InvalidLogsBloom: Logs bloom in header does not match. Expected {expected}, got {actual}";

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

    public static string InvalidReceiptsRoot(Hash256 expected, Hash256 actual) =>
        $"InvalidReceiptsRoot: Receipts root in header does not match. Expected {expected}, got {actual}";

    public static string InvalidStateRoot(Hash256 expected, Core.Crypto.Hash256 actual) =>
        $"InvalidStateRoot: State root in header does not match. Expected {expected}, got {actual}";

    public static string InvalidParentBeaconBlockRoot(Hash256 expected, Hash256 actual) =>
        $"InvalidParentBeaconBlockRoot: Beacon block root in header does not match. Expected {expected}, got {actual}";

    public const string BlobGasPriceOverflow =
        "BlobGasPriceOverflow: Overflow in excess blob gas.";

    public static string InvalidHeaderHash(Hash256 expected, Hash256 actual) =>
        $"InvalidHeaderHash: Header hash does not match. Expected {expected}, got {actual}";

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

    public static string InvalidBaseFeePerGas(UInt256? expected, UInt256 actual) =>
        $"InvalidBaseFeePerGas: Does not match calculated. Expected {expected}, got {actual}";

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

    public static string HeaderGasUsedMismatch(long expected, long actual) =>
        $"HeaderGasUsedMismatch: Gas used in header does not match calculated. Expected {expected}, got {actual}";

    public static string BlobGasUsedAboveBlockLimit(ulong blobGas, int blobsCount, ulong blobsGasUsed) =>
        $"BlockBlobGasExceeded: A block cannot have more than {blobGas} blob gas, blobs count {blobsCount}, blobs gas used: {blobsGasUsed}.";

    public static string IncorrectExcessBlobGas(ulong? expected, ulong? actual) =>
        $"HeaderExcessBlobGasMismatch: Excess blob gas in header does not match calculated. Expected {expected}, got {actual}";

    public static string HeaderBlobGasMismatch(ulong? expected, ulong? actual) =>
        $"HeaderBlobGasMismatch: Blob gas in header does not match calculated. Expected {expected}, got {actual}";

    public const string InvalidTimestamp =
        "InvalidTimestamp: Timestamp in header cannot be lower than ancestor.";

    public const string NegativeBlockNumber =
        "NegativeBlockNumber: Block number cannot be negative.";

    public const string NegativeGasLimit =
        "NegativeGasLimit: Gas limit cannot be negative.";

    public const string NegativeGasUsed =
        "NegativeGasUsed: Cannot be negative.";

    public const string MissingRequests =
        "MissingRequests: Requests cannot be null in block when EIP-6110 or EIP-7002 are activated.";

    public const string RequestsNotEnabled =
        "RequestsNotEnabled: Requests must be null in block when EIP-6110 and EIP-7002 are not activated.";

    public static string InvalidRequestsHash(Hash256? expected, Hash256? actual) =>
        $"InvalidRequestsHash: Requests hash mismatch in block: expected {expected}, got {actual}";

    public const string InvalidRequestsOrder =
        "InvalidRequestsOrder: Requests are not in the correct order in block.";


    public const string WithdrawalsContractEmpty =
        "WithdrawalsEmpty: Contract is not deployed.";

    public const string WithdrawalsContractFailed =
        "WithdrawalsFailed: Contract execution failed.";

    public const string ConsolidationsContractEmpty =
        "ConsolidationsEmpty: Contract is not deployed.";

    public const string ConsolidationsContractFailed =
        "ConsolidationsFailed: Contract execution failed.";

    public static string InvalidDepositEventLayout(string error) =>
        $"DepositsInvalid: Invalid deposit event layout: {error}";
}
