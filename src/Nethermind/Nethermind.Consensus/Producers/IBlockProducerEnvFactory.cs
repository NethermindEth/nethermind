// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Transactions;

namespace Nethermind.Consensus.Producers
{
    public interface IBlockProducerEnvFactory
    {
        IBlockTransactionsExecutorFactory TransactionsExecutorFactory { get; set; }
        BlockProducerEnv Create(ITxSource? additionalTxSource = null);
    }
}
