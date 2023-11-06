// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Config;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Transactions;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Trie.Pruning;
using Nethermind.TxPool;

namespace Nethermind.Optimism;

public class OptimismBlockProducerEnvFactory : BlockProducerEnvFactory
{
    private readonly ChainSpec _chainSpec;
    private readonly OPSpecHelper _specHelper;
    private readonly OPL1CostHelper _l1CostHelper;

    public OptimismBlockProducerEnvFactory(
        ChainSpec chainSpec,
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
        OPSpecHelper specHelper,
        OPL1CostHelper l1CostHelper,
        ILogManager logManager) : base(dbProvider,
        blockTree, readOnlyTrieStore, specProvider, blockValidator,
        rewardCalculatorSource, receiptStorage, blockPreprocessorStep,
        txPool, transactionComparerProvider, blocksConfig, logManager)
    {
        _specHelper = specHelper;
        _l1CostHelper = l1CostHelper;
        _chainSpec = chainSpec;
        TransactionsExecutorFactory = new OptimismTransactionsExecutorFactory(specProvider, logManager);
    }

    protected override ReadOnlyTxProcessingEnv CreateReadonlyTxProcessingEnv(ReadOnlyDbProvider readOnlyDbProvider,
        ReadOnlyBlockTree readOnlyBlockTree)
    {
        ReadOnlyTxProcessingEnv result = new(readOnlyDbProvider, _readOnlyTrieStore,
            readOnlyBlockTree, _specProvider, _logManager);
        result.TransactionProcessor =
            new OptimismTransactionProcessor(_specProvider, result.StateProvider, result.Machine, _logManager, _l1CostHelper, _specHelper, result.CodeInfoRepository);

        return result;
    }

    protected override ITxSource CreateTxSourceForProducer(ITxSource? additionalTxSource,
        ReadOnlyTxProcessingEnv processingEnv,
        ITxPool txPool, IBlocksConfig blocksConfig, ITransactionComparerProvider transactionComparerProvider,
        ILogManager logManager)
    {
        ITxSource baseTxSource = base.CreateTxSourceForProducer(additionalTxSource, processingEnv, txPool, blocksConfig,
            transactionComparerProvider, logManager);

        return new OptimismTxPoolTxSource(baseTxSource);
    }
}
