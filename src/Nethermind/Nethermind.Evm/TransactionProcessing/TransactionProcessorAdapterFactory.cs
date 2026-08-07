// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm.TransactionProcessing;

/// <summary>Builds the <see cref="ITransactionProcessorAdapter"/> wrapping a given tx processor.</summary>
/// <remarks>
/// Registered explicitly per scope (default <see cref="ExecuteTransactionProcessorAdapter"/> on the block-processing
/// path, the simulate adapter on the <c>eth_simulate</c> path), so callers select the adapter behaviour by scope.
/// </remarks>
public delegate ITransactionProcessorAdapter TransactionProcessorAdapterFactory(ITransactionProcessor transactionProcessor);
