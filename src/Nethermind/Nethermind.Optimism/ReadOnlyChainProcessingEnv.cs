// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Validators;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core.Specs;
using Nethermind.Logging;

namespace Nethermind.Optimism;

/// <summary>
/// Not thread safe.
/// </summary>
public class OptimismReadOnlyChainProcessingEnv(
    ReadOnlyTxProcessingEnv txEnv,
    IBlockValidator blockValidator,
    IBlockPreprocessorStep recoveryStep,
    IRewardCalculator rewardCalculator,
    IReceiptStorage receiptStorage,
    ISpecProvider specProvider,
    ILogManager logManager,
    IOptimismSpecHelper opSpecHelper,
    Create2DeployerContractRewriter contractRewriter,
    IWithdrawalProcessor? withdrawalProcessor,
    IBlockProcessor.IBlockTransactionsExecutor? blockTransactionsExecutor = null) : ReadOnlyChainProcessingEnv(
    txEnv,
    blockValidator,
    recoveryStep,
    rewardCalculator,
    receiptStorage,
    specProvider,
    logManager,
    blockTransactionsExecutor)
{
    protected override IBlockProcessor CreateBlockProcessor(ReadOnlyTxProcessingEnv txEnv, IBlockValidator blockValidator, IRewardCalculator rewardCalculator, IReceiptStorage receiptStorage, ISpecProvider specProvider, ILogManager logManager, IBlockProcessor.IBlockTransactionsExecutor transactionsExecutor)
    {
        return new OptimismBlockProcessor(
            specProvider,
            blockValidator,
            rewardCalculator,
            transactionsExecutor,
            StateProvider,
            receiptStorage,
            new BlockhashStore(txEnv.BlockTree, specProvider, StateProvider),
            logManager,
            opSpecHelper,
            contractRewriter,
            withdrawalProcessor);
    }
}
