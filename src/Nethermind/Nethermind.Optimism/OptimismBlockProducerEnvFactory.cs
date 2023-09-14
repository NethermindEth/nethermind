// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Config;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Validators;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.Trie.Pruning;
using Nethermind.TxPool;

namespace Nethermind.Optimism;

public class OptimismBlockProducerEnvFactory : BlockProducerEnvFactory
{
    private ITransactionProcessorFactory _txProcessorFactory;

    public OptimismBlockProducerEnvFactory(
        ITransactionProcessorFactory transactionProcessorFactory,
        IDbProvider dbProvider,
        IBlockTree blockTree,
        IReadOnlyTrieStore readOnlyTrieStore,
        ISpecProvider specProvider,
        IBlockValidator blockValidator,
        IRewardCalculatorSource rewardCalculatorSource,
        IReceiptStorage receiptStorage,
        IBlockPreprocessorStep blockPreprocessorStep,
        ITxPool txPool,
        ITransactionComparerProvider transactionComparerProvider,
        IBlocksConfig blocksConfig,
        ILogManager logManager) : base(dbProvider,
            blockTree, readOnlyTrieStore, specProvider, blockValidator,
            rewardCalculatorSource, receiptStorage, blockPreprocessorStep,
            txPool, transactionComparerProvider, blocksConfig, logManager)
    {
        _txProcessorFactory = transactionProcessorFactory;
    }

    protected override ReadOnlyTxProcessingEnv CreateReadonlyTxProcessingEnv(ReadOnlyDbProvider readOnlyDbProvider,
        ReadOnlyBlockTree readOnlyBlockTree) => new(readOnlyDbProvider, _readOnlyTrieStore, readOnlyBlockTree, _specProvider, _logManager, _txProcessorFactory);
}
