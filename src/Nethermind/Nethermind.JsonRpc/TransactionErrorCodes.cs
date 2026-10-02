// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.JsonRpc;

/// <summary>
/// The JSON-RPC error codes of a rejected unsigned call, shared by eth_simulateV1 and the trace call methods so the same
/// rejection gets the same code.
/// </summary>
internal static class TransactionErrorCodes
{
    /// <summary>Returns the code for <paramref name="error"/>, or <see langword="null"/> when it has none of its own.</summary>
    public static int? Get(TransactionResult.ErrorType error) => error switch
    {
        TransactionResult.ErrorType.BlockGasLimitExceeded => ErrorCodes.BlockGasLimitReached,
        TransactionResult.ErrorType.GasLimitBelowIntrinsicGas => ErrorCodes.IntrinsicGas,
        TransactionResult.ErrorType.GasLimitExceedsMaxTotalCap => ErrorCodes.InvalidInput,
        TransactionResult.ErrorType.InsufficientMaxFeePerGasForSenderBalance
            or TransactionResult.ErrorType.InsufficientSenderBalance => ErrorCodes.InsufficientFunds,
        TransactionResult.ErrorType.MaxFeePerGasBelowBaseFee
            or TransactionResult.ErrorType.MinerPremiumNegative => ErrorCodes.FeeCapBelowBaseFee,
        TransactionResult.ErrorType.SenderHasDeployedCode => ErrorCodes.SenderIsNotEoa,
        TransactionResult.ErrorType.TransactionSizeOverMaxInitCodeSize => ErrorCodes.MaxInitCodeSizeExceeded,
        TransactionResult.ErrorType.TransactionNonceTooHigh => ErrorCodes.NonceTooHigh,
        TransactionResult.ErrorType.TransactionNonceTooLow => ErrorCodes.NonceTooLow,
        _ => null
    };
}
