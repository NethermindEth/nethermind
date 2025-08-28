// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Validators;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules.DebugModule;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Synchronization.ParallelSync;
using System.IO.Abstractions;
using Nethermind.Core;
using Nethermind.Taiko.BlockTransactionExecutors;
using Nethermind.Facade;

namespace Nethermind.Taiko.Rpc;

class TaikoDebugModuleFactory :
    DebugModuleFactory
{
    public TaikoDebugModuleFactory(
        IWorldStateManager worldStateManager,
        IDbProvider dbProvider,
        IBlockTree blockTree,
        IJsonRpcConfig jsonRpcConfig,
        IBlockchainBridge blockchainBridge,
        ulong secondsPerSlot,
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
        ILogManager logManager,
        IPrecompileChecker precompileChecker) : base(worldStateManager, dbProvider, blockTree, jsonRpcConfig, blockchainBridge, secondsPerSlot, blockValidator, recoveryStep, rewardCalculator, receiptStorage, receiptsMigration, configProvider, specProvider, syncModeSelector, badBlockStore, fileSystem, logManager, precompileChecker)
    {
    }

    protected override IBlockProcessor.IBlockTransactionsExecutor CreateBlockTransactionsExecutor(ChangeableTransactionProcessorAdapter transactionProcessorAdapter, IWorldState worldState)
        => new TaikoBlockValidationTransactionExecutor(transactionProcessorAdapter, worldState);
}
