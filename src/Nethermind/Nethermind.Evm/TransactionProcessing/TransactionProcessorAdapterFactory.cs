// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm.TransactionProcessing;

/// <summary>Builds the <see cref="ITransactionProcessorAdapter"/> wrapping the block-access-list pool's tx processors.</summary>
/// <remarks>
/// The block-processing scope registers the default (<see cref="ExecuteTransactionProcessorAdapter"/>); a scope that
/// needs different per-transaction behaviour on the EIP-7928 BAL path registers its own. This does not cover scopes
/// that merely override <see cref="ITransactionProcessorAdapter"/> (trace, proof, block production, …) — those still
/// get the default here on the BAL path.
/// </remarks>
public delegate ITransactionProcessorAdapter TransactionProcessorAdapterFactory(ITransactionProcessor transactionProcessor);
