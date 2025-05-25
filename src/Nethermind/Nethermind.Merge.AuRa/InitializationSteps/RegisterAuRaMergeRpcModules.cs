// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus;
using Nethermind.Consensus.AuRa;
using Nethermind.Consensus.AuRa.InitializationSteps;
using Nethermind.Consensus.AuRa.Validators;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Transactions;
using Nethermind.Consensus.Validators;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core.Specs;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Merge.AuRa.InitializationSteps;

public class RegisterAuRaMergeRpcModules(AuRaNethermindApi api, IPoSSwitcher poSSwitcher) : RegisterAuRaRpcModules(api, poSSwitcher)
{
    protected override IAuRaBlockProcessorFactory CreateFactory()
    {
        return new AuRaMergeBlockProcessorFactory();
    }

    private class AuRaMergeBlockProcessorFactory : IAuRaBlockProcessorFactory
    {
        public AuRaBlockProcessor Create(ISpecProvider specProvider, IBlockValidator blockValidator,
            IRewardCalculator rewardCalculator, IBlockProcessor.IBlockTransactionsExecutor blockTransactionsExecutor, IWorldState stateProvider,
            IReceiptStorage receiptStorage, IBeaconBlockRootHandler beaconBlockRootHandler, ILogManager logManager,
            IBlockFinder blockTree, IWithdrawalProcessor withdrawalProcessor, IExecutionRequestsProcessor executionRequestsProcessor,
            IAuRaValidator? auRaValidator, ITxFilter? txFilter = null, AuRaContractGasLimitOverride? gasLimitOverride = null,
            ContractRewriter? contractRewriter = null, IBlockCachePreWarmer? preWarmer = null)
        {
            return new AuRaMergeBlockProcessor(specProvider, blockValidator, rewardCalculator, blockTransactionsExecutor,
                stateProvider, receiptStorage, beaconBlockRootHandler, logManager, blockTree, withdrawalProcessor,
                executionRequestsProcessor, auRaValidator, txFilter, gasLimitOverride, contractRewriter, preWarmer);
        }
    }
}
