// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.AuRa.Validators;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Validators;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Consensus.AuRa.InitializationSteps;

public interface IAuRaBlockProcessorFactory
{
    public AuRaBlockProcessor Create(
        IBlockValidator blockValidator,
        IRewardCalculator rewardCalculator,
        IBlockProcessor.IBlockTransactionsExecutor blockTransactionsExecutor,
        IWorldState stateProvider,
        IReceiptStorage receiptStorage,
        IBeaconBlockRootHandler beaconBlockRootHandler,
        ITransactionProcessor transactionProcessor,
        IExecutionRequestsProcessor executionRequestsProcessor,
        IAuRaValidator? auRaValidator,
        IBlockCachePreWarmer? preWarmer = null);
}
