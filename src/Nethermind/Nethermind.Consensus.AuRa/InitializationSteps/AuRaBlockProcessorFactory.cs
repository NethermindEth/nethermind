// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Consensus.AuRa.Transactions;
using Nethermind.Consensus.AuRa.Validators;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Transactions;
using Nethermind.Consensus.Validators;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;

namespace Nethermind.Consensus.AuRa.InitializationSteps;

public class AuRaBlockProcessorFactory(
    AuRaChainSpecEngineParameters parameters,
    IBlockTree blockTree,
    ISpecProvider specProvider,
    AuRaGasLimitOverrideFactory gasLimitOverrideFactory,
    TxAuRaFilterBuilders txAuRaFilterBuilders,
    ILogManager logManager
) : IAuRaBlockProcessorFactory
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
        IBlockCachePreWarmer? preWarmer = null)
    {
        IDictionary<long, IDictionary<Address, byte[]>> rewriteBytecode = parameters.RewriteBytecode;
        ContractRewriter? contractRewriter = rewriteBytecode?.Count > 0 ? new ContractRewriter(rewriteBytecode) : null;

        ITxFilter txFilter = txAuRaFilterBuilders.CreateAuRaTxFilter(new ServiceTxFilter(specProvider));

        return new AuRaBlockProcessor(
            specProvider,
            blockValidator,
            rewardCalculator,
            blockTransactionsExecutor,
            stateProvider,
            receiptStorage,
            beaconBlockRootHandler,
            logManager,
            blockTree,
            NullWithdrawalProcessor.Instance,
            executionRequestsProcessor,
            auRaValidator,
            txFilter,
            gasLimitOverrideFactory.GetGasLimitCalculator(),
            contractRewriter, preWarmer);
    }
}
