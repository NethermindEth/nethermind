// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Consensus.ExecutionRequests;

/// <summary>
/// Creates an <see cref="IExecutionRequestsProcessor"/> for use with a specific
/// <see cref="ITransactionProcessor"/>, allowing callers to substitute a variant
/// (e.g. the stateless one) without the consumer knowing about the mode.
/// </summary>
public interface IExecutionRequestsProcessorFactory
{
    IExecutionRequestsProcessor Create(ITransactionProcessor transactionProcessor);
}
