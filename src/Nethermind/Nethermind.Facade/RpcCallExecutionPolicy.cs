// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Facade;

/// <summary>Immutable, chain-selected fee semantics for the restoring calls of <c>eth_call</c> and <c>eth_createAccessList</c>.</summary>
/// <remarks>
/// Defaults preserve Ethereum's existing call behaviour. This policy does not change gas metering,
/// GASPRICE, BASEFEE, value transfers, estimates, traces, simulated blocks or consensus execution.
/// Typed fee caps are validated before execution even when transaction fees are not charged.
/// </remarks>
public sealed class RpcCallExecutionPolicy(bool chargeFees = true, bool validateLegacyGasPriceAgainstBaseFee = true)
{
    public bool ChargeFees { get; } = chargeFees;
    public bool ValidateLegacyGasPriceAgainstBaseFee { get; } = validateLegacyGasPriceAgainstBaseFee;

    internal ExecutionOptions Options { get; } = ExecutionOptions.CommitAndRestore
        | (chargeFees ? ExecutionOptions.None : ExecutionOptions.SkipTransactionFees)
        | (validateLegacyGasPriceAgainstBaseFee ? ExecutionOptions.None : ExecutionOptions.SkipLegacyGasPriceValidation);
}
