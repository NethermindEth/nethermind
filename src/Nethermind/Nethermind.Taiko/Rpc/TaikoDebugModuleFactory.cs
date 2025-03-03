// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Tracing;
using Nethermind.Consensus.Validators;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules.DebugModule;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Trie.Pruning;
using System.IO.Abstractions;

namespace Nethermind.Taiko.Rpc;

class TaikoDebugModuleFactory :
    DebugModuleFactory
{
    public TaikoDebugModuleFactory(
        IReadOnlyTrieStore trieStore,
        IDbProvider dbProvider,
        IBlockTree blockTree,
        IJsonRpcConfig jsonRpcConfig,
        IBlockValidator blockValidator,
        IBlockPreprocessorStep recoveryStep,
        IRewardCalculatorSource rewardCalculator,
        IReceiptStorage receiptStorage,
        IReceiptsMigration receiptsMigration,
        IConfigProvider configProvider,
        ISpecProvider specProvider,
        ISyncModeSelector syncModeSelector,
        IBadBlockStore badBlockStore,
        IFileSystem fileSystem,
        ILogManager logManager) : base(trieStore, dbProvider, blockTree, jsonRpcConfig, blockValidator, recoveryStep, rewardCalculator, receiptStorage, receiptsMigration, configProvider, specProvider, syncModeSelector, badBlockStore, fileSystem, logManager)
    {
    }

    protected override ReadOnlyChainProcessingEnv CreateReadOnlyChainProcessingEnv(
    IReadOnlyTxProcessingScope scope,
    OverridableWorldStateManager worldStateManager,
    IBlockProcessor.IBlockTransactionsExecutor transactionsExecutor)
    {
        return new ReadOnlyChainProcessingEnv(
            scope,
            _blockValidator,
            _recoveryStep,
            _rewardCalculatorSource.Get(scope.TransactionProcessor),
            _receiptStorage,
            _specProvider,
            _blockTree,
            worldStateManager.GlobalStateReader,
            _logManager,
            new BlockInvalidTxExecutor(scope.TransactionProcessor, scope.WorldState));
    }
}
