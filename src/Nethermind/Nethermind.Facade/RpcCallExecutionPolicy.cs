// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Facade;

/// <summary>Immutable, chain-selected fee semantics for <c>eth_call</c> only.</summary>
/// <remarks>
/// Defaults preserve Ethereum's existing call behaviour. This policy does not change gas metering,
/// GASPRICE, BASEFEE, value transfers, estimates, traces, simulated blocks or consensus execution.
/// Typed fee caps are validated before execution even when transaction fees are not charged.
/// </remarks>
public sealed class RpcCallExecutionPolicy(bool chargeFees = true, bool validateLegacyGasPriceAgainstBaseFee = true)
{
    /// <summary>Whether the call buys its gas up front, refunds the unused part and pays the fee recipients.</summary>
    /// <remarks>
    /// When <see langword="false"/>, the sender needs funds for the transferred value only and no account is debited
    /// or credited for gas.
    /// </remarks>
    public bool ChargeFees { get; } = chargeFees;

    /// <summary>Whether a legacy <c>gasPrice</c> below the block base fee fails the call.</summary>
    /// <remarks>Does not change how EIP-1559 fee caps are validated.</remarks>
    public bool ValidateLegacyGasPriceAgainstBaseFee { get; } = validateLegacyGasPriceAgainstBaseFee;

    internal ExecutionOptions Options { get; } = ExecutionOptions.CommitAndRestore
        | (chargeFees ? ExecutionOptions.None : ExecutionOptions.SkipTransactionFees)
        | (validateLegacyGasPriceAgainstBaseFee ? ExecutionOptions.None : ExecutionOptions.SkipLegacyGasPriceValidation);
}
