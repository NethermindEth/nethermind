// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Consensus.Processing;

/// <summary>
/// Extra <see cref="ExecutionOptions"/> OR-ed into every transaction the <see cref="BlockAccessListManager"/>
/// runs through its own tx processors (the EIP-7928 BAL path, which bypasses the normal adapter).
/// </summary>
/// <remarks>
/// Registered in the <c>eth_simulateV1</c> scope with <see cref="ExecutionOptions.SkipSenderCodeCheck"/> so a
/// state-overridden contract can be the sender on the BAL path too, without wrapping (and type-erasing) the
/// release spec. Absent (or <see cref="None"/>) everywhere else, so real block production keeps EIP-3607 enforced.
/// </remarks>
public sealed record BlockAccessListTxExecutionOptions(ExecutionOptions AdditionalOptions)
{
    public static readonly BlockAccessListTxExecutionOptions None = new(ExecutionOptions.None);
}
