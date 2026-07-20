// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Consensus.ExecutionRequests;

/// <inheritdoc cref="IExecutionRequestsProcessorFactory"/>
public sealed class ExecutionRequestsProcessorFactory : IExecutionRequestsProcessorFactory
{
    public static ExecutionRequestsProcessorFactory Instance { get; } = new();

    public IExecutionRequestsProcessor Create(ITransactionProcessor transactionProcessor) =>
        new ExecutionRequestsProcessor(transactionProcessor);
}
