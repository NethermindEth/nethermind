// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Processing;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Consensus.Producers
{
    public interface IBlockTransactionsExecutorFactory
    {
        IBlockProcessor.IBlockTransactionsExecutor Create(IReadOnlyTxProcessingScope readOnlyTxProcessingEnv);
    }
}
