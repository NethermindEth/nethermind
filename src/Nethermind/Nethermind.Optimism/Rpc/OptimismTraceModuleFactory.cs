// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Validators;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core.Specs;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules.Trace;
using Nethermind.Logging;
using Nethermind.Facade;
using Nethermind.State;

namespace Nethermind.Optimism.Rpc;

public class OptimismTraceModuleFactory(
    IWorldStateManager worldStateManager,
    IBlockTree blockTree,
    IJsonRpcConfig jsonRpcConfig,
    IBlockchainBridge blockchainBridge,
    ulong secondsPerSlot,
    IBlockPreprocessorStep recoveryStep,
    IRewardCalculatorSource rewardCalculatorSource,
    IReceiptStorage receiptFinder,
    ISpecProvider specProvider,
    IPoSSwitcher poSSwitcher,
    ILogManager logManager,
    ICostHelper costHelper,
    IOptimismSpecHelper opSpecHelper,
    Create2DeployerContractRewriter contractRewriter,
    IWithdrawalProcessor withdrawalProcessor) : TraceModuleFactory(
        worldStateManager,
        blockTree,
        jsonRpcConfig,
        blockchainBridge,
        secondsPerSlot,
        recoveryStep,
        rewardCalculatorSource,
        receiptFinder,
        specProvider,
        poSSwitcher,
        logManager)
{
    protected override OverridableTxProcessingEnv CreateTxProcessingEnv(IOverridableWorldScope worldStateManager) =>
        new OptimismOverridableTxProcessingEnv(worldStateManager, _blockTree, _specProvider, _logManager, costHelper, opSpecHelper);

    protected override ReadOnlyChainProcessingEnv CreateChainProcessingEnv(IOverridableWorldScope worldStateManager, IBlockProcessor.IBlockTransactionsExecutor transactionsExecutor, IReadOnlyTxProcessingScope scope, IRewardCalculator rewardCalculator) => new OptimismReadOnlyChainProcessingEnv(
                scope,
                Always.Valid,
                _recoveryStep,
                rewardCalculator,
                _receiptStorage,
                _specProvider,
                _blockTree,
                worldStateManager.GlobalStateReader,
                _logManager,
                opSpecHelper,
                contractRewriter,
                withdrawalProcessor,
                transactionsExecutor);
}
