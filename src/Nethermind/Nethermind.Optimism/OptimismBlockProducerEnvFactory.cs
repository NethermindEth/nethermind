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
        ILogManager logManager) : base(dbProvider,
        blockTree, readOnlyTrieStore, specProvider, blockValidator,
        rewardCalculatorSource, receiptStorage, blockPreprocessorStep,
        txPool, transactionComparerProvider, blocksConfig, logManager)
    {
        _chainSpec = chainSpec;
        TransactionsExecutorFactory = new OptimismTransactionsExecutorFactory(specProvider, logManager);
    }

    protected override ReadOnlyTxProcessingEnv CreateReadonlyTxProcessingEnv(ReadOnlyDbProvider readOnlyDbProvider,
        ReadOnlyBlockTree readOnlyBlockTree)
    {
        // TODO: get from chainspec
        Address l1FeeRecipient = new("0x420000000000000000000000000000000000001A");
        Address l1BlockAddress = new("0x4200000000000000000000000000000000000015");

        OPSpecHelper opConfigHelper = new(
            _chainSpec.Optimism.RegolithTimestamp,
            _chainSpec.Optimism.BedrockBlockNumber,
            l1FeeRecipient // it would be good to get this last one from chainspec too
        );
        OPL1CostHelper l1CostHelper = new(opConfigHelper, l1BlockAddress);
        OptimismTransactionProcessorFactory txProcessorFactory = new(l1CostHelper, opConfigHelper);

        return new ReadOnlyTxProcessingEnv(readOnlyDbProvider, _readOnlyTrieStore, readOnlyBlockTree, _specProvider,
            _logManager, txProcessorFactory);
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
