// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Abi;
using Nethermind.Blockchain;
using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.AuRa;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Consensus.AuRa.InitializationSteps;
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
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.Merge.AuRa.Withdrawals;
using Nethermind.State;

namespace Nethermind.Merge.AuRa.InitializationSteps;

public class AuRaMergeBlockProcessorFactory(
    AuRaChainSpecEngineParameters parameters,
    ISpecProvider specProvider,
    IAbiEncoder abiEncoder,
    IBlockTree blockTree,
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

        WithdrawalContractFactory withdrawalContractFactory = new WithdrawalContractFactory(parameters, abiEncoder);
        IWithdrawalProcessor withdrawalProcessor = new AuraWithdrawalProcessor(withdrawalContractFactory.Create(transactionProcessor), logManager);

        ITxFilter txFilter = txAuRaFilterBuilders.CreateAuRaTxFilter(new ServiceTxFilter());

        return new AuRaMergeBlockProcessor(
            specProvider,
            blockValidator,
            rewardCalculator,
            blockTransactionsExecutor,
            stateProvider,
            receiptStorage,
            beaconBlockRootHandler,
            logManager,
            blockTree,
            withdrawalProcessor,
            executionRequestsProcessor,
            auRaValidator,
            txFilter,
            gasLimitOverrideFactory.GetGasLimitCalculator(),
            contractRewriter,
            preWarmer);
    }
}
