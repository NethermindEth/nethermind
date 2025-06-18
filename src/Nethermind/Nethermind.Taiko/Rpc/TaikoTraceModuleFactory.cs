// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Rewards;
using Nethermind.Core.Specs;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules.Trace;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Facade;
using Nethermind.Taiko.BlockTransactionExecutors;

namespace Nethermind.Taiko.Rpc;

class TaikoTraceModuleFactory(
    IWorldStateManager worldStateManager,
    IBlockTree blockTree, IJsonRpcConfig jsonRpcConfig, IBlockchainBridge blockchainBridge, ulong secondsPerSlot,
    IBlockPreprocessorStep recoveryStep, IRewardCalculatorSource rewardCalculatorSource, IReceiptStorage receiptFinder,
    ISpecProvider specProvider, IPoSSwitcher poSSwitcher, ILogManager logManager) :
    TraceModuleFactory(worldStateManager, blockTree, jsonRpcConfig, blockchainBridge, secondsPerSlot, recoveryStep, rewardCalculatorSource, receiptFinder, specProvider, poSSwitcher, logManager)
{
    protected override OverridableTxProcessingEnv CreateTxProcessingEnv(IOverridableWorldScope worldStateManager)
        => new TaikoOverridableTxProcessingEnv(worldStateManager, _blockTree, _specProvider, _logManager);

    protected override IBlockProcessor.IBlockTransactionsExecutor CreateBlockTransactionsExecutor(IReadOnlyTxProcessingScope scope)
        => new TaikoBlockValidationTransactionExecutor(new ExecuteTransactionProcessorAdapter(scope.TransactionProcessor), scope.WorldState);

    protected override IBlockProcessor.IBlockTransactionsExecutor CreateRpcBlockTransactionsExecutor(IReadOnlyTxProcessingScope scope)
        => new TaikoBlockValidationTransactionExecutor(new TraceTransactionProcessorAdapter(scope.TransactionProcessor), scope.WorldState);
}
