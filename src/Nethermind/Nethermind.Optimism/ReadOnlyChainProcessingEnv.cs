// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Validators;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core.Specs;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Optimism;

/// <summary>
/// Not thread safe.
/// </summary>
public class OptimismReadOnlyChainProcessingEnv(
    IReadOnlyTxProcessingScope txEnv,
    IBlockValidator blockValidator,
    IBlockPreprocessorStep recoveryStep,
    IRewardCalculator rewardCalculator,
    IReceiptStorage receiptStorage,
    ISpecProvider specProvider,
    IBlockTree blockTree,
    IStateReader stateReader,
    ILogManager logManager,
    IOptimismSpecHelper opSpecHelper,
    Create2DeployerContractRewriter contractRewriter,
    IWithdrawalProcessor withdrawalProcessor,
    IBlockProcessor.IBlockTransactionsExecutor? blockTransactionsExecutor = null) : ReadOnlyChainProcessingEnv(
    txEnv,
    blockValidator,
    recoveryStep,
    rewardCalculator,
    receiptStorage,
    specProvider,
    blockTree,
    stateReader,
    logManager,
    blockTransactionsExecutor)
{

    protected override IBlockProcessor CreateBlockProcessor(
        IReadOnlyTxProcessingScope scope,
        IBlockTree blockTree,
        IBlockValidator blockValidator,
        IRewardCalculator rewardCalculator,
        IReceiptStorage receiptStorage,
        ISpecProvider specProvider,
        ILogManager logManager,
        IBlockProcessor.IBlockTransactionsExecutor transactionsExecutor
    )
    {
        return new OptimismBlockProcessor(
            specProvider,
            blockValidator,
            rewardCalculator,
            transactionsExecutor,
            scope.WorldState,
            receiptStorage,
            new BlockhashStore(specProvider, scope.WorldState),
            new BeaconBlockRootHandler(scope.TransactionProcessor, scope.WorldState),
            logManager,
            opSpecHelper,
            contractRewriter,
            withdrawalProcessor,
            new ExecutionRequestsProcessor(scope.TransactionProcessor));
    }
}
