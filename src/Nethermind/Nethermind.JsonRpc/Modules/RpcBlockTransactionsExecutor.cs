// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Processing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.State;

namespace Nethermind.JsonRpc.Modules
{
    public class RpcBlockTransactionsExecutor(ITransactionProcessor transactionProcessor, IWorldState stateProvider)
        : BlockProcessor.BlockValidationTransactionsExecutor(new TraceTransactionProcessorAdapter(transactionProcessor),
            stateProvider);
}
