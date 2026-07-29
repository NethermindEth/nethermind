// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Consensus.Stateless;

/// <summary>Creates <see cref="StatelessExecutionRequestsProcessor"/> for stateless re-execution.</summary>
public sealed class StatelessExecutionRequestsProcessorFactory : IExecutionRequestsProcessorFactory
{
    public static StatelessExecutionRequestsProcessorFactory Instance { get; } = new();

    public IExecutionRequestsProcessor Create(ITransactionProcessor transactionProcessor) =>
        new StatelessExecutionRequestsProcessor(transactionProcessor);
}
