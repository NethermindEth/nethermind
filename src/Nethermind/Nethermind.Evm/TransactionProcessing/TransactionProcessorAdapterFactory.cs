// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm.TransactionProcessing;

/// <summary>Builds the <see cref="ITransactionProcessorAdapter"/> for a given <see cref="ITransactionProcessor"/>.</summary>
/// <remarks>
/// The single axis for per-transaction adapter selection: the block-processing scope registers the default
/// (<see cref="ExecuteTransactionProcessorAdapter"/>) and derives the scoped <see cref="ITransactionProcessorAdapter"/>
/// from it, so a scope that overrides this factory (block production, trace, proof, simulate) changes both the main
/// path and the EIP-7928 BAL pool's per-worker adapters at once. The one exception is debug, which registers its
/// runtime-mutable <see cref="ChangeableTransactionProcessorAdapter"/> directly and keeps the default behaviour on
/// the BAL path.
/// </remarks>
public delegate ITransactionProcessorAdapter TransactionProcessorAdapterFactory(ITransactionProcessor transactionProcessor);
