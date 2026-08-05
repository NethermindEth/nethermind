// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Consensus.Processing;

/// <summary>
/// Extra <see cref="ExecutionOptions"/> OR-ed into every transaction the <see cref="BlockAccessListManager"/>
/// runs through its own tx processors (the EIP-7928 BAL path). Registered only in the eth_simulateV1 scope;
/// absent elsewhere, so real block production keeps EIP-3607 enforced.
/// </summary>
public sealed record BlockAccessListTxExecutionOptions(ExecutionOptions AdditionalOptions)
{
    public static readonly BlockAccessListTxExecutionOptions None = new(ExecutionOptions.None);
}
