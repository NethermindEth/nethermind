// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable
using Nethermind.Blockchain;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.FullPruning;
using Nethermind.Blockchain.Services;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Facade;
using Nethermind.Facade.Eth;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules.Eth.GasPrice;
using Nethermind.State;
using Nethermind.Trie.ByPath;
using Nethermind.Trie.Pruning;
using Nethermind.TxPool;

namespace Nethermind.Api
{
    public interface IApiWithBlockchain : IApiWithStores, IBlockchainBridgeFactory
    {
        (IApiWithStores GetFromApi, IApiWithBlockchain SetInApi) ForInit => (this, this);
        (IApiWithStores GetFromApi, IApiWithBlockchain SetInApi) ForBlockchain => (this, this);
        (IApiWithBlockchain GetFromApi, IApiWithBlockchain SetInApi) ForProducer => (this, this);

        IBlockchainProcessor? BlockchainProcessor { get; set; }
        CompositeBlockPreprocessorStep BlockPreprocessor { get; }
        IBlockProcessingQueue? BlockProcessingQueue { get; set; }
        IBlockProcessor? MainBlockProcessor { get; set; }
        IBlockProducer? BlockProducer { get; set; }
        IBlockValidator? BlockValidator { get; set; }
        IEnode? Enode { get; set; }
        IFilterStore? FilterStore { get; set; }
        IFilterManager? FilterManager { get; set; }
        IUnclesValidator? UnclesValidator { get; set; }
        IHeaderValidator? HeaderValidator { get; set; }
        IManualBlockProductionTrigger ManualBlockProductionTrigger { get; }
        IReadOnlyTrieStore? ReadOnlyTrieStore { get; set; }
        IReadOnlyTrieStore? ReadOnlyStorageTrieStore { get; set; }
        IRewardCalculatorSource? RewardCalculatorSource { get; set; }
        /// <summary>
        /// PoS switcher for The Merge
        /// </summary>
        IPoSSwitcher PoSSwitcher { get; set; }
        ISealer? Sealer { get; set; }
        ISealValidator? SealValidator { get; set; }
        ISealEngine SealEngine { get; set; }
        /// <summary>
        /// Can be used only for processing blocks, on all other contexts use <see cref="StateReader"/> or <see cref="ChainHeadStateProvider"/>.
        /// </summary>
        /// <remarks>
        /// DO NOT USE OUTSIDE OF PROCESSING BLOCK CONTEXT!
        /// </remarks>
        IWorldState? WorldState { get; set; }
        IKeyValueStoreWithBatching? MainStateDbWithCache { get; set; }
        IReadOnlyStateProvider? ChainHeadStateProvider { get; set; }
        IStateReader? StateReader { get; set; }
        ITransactionProcessor? TransactionProcessor { get; set; }
        ITrieStore? TrieStore { get; set; }
        ITxSender? TxSender { get; set; }
        INonceManager? NonceManager { get; set; }
        ITxPool? TxPool { get; set; }
        ITxPoolInfoProvider? TxPoolInfoProvider { get; set; }
        CompositeTxGossipPolicy TxGossipPolicy { get; }
        IWitnessCollector? WitnessCollector { get; set; }
        IWitnessRepository? WitnessRepository { get; set; }
        IHealthHintService? HealthHintService { get; set; }
        IRpcCapabilitiesProvider? RpcCapabilitiesProvider { get; set; }
        ITransactionComparerProvider? TransactionComparerProvider { get; set; }
        TxValidator? TxValidator { get; set; }

        /// <summary>
        /// Manager of block finalization
        /// </summary>
        /// <remarks>
        /// Currently supported in <see cref="SealEngineType.AuRa"/> and Eth2Merge.
        /// </remarks>
        IBlockFinalizationManager? FinalizationManager { get; set; }

        IGasLimitCalculator? GasLimitCalculator { get; set; }

        IBlockProducerEnvFactory? BlockProducerEnvFactory { get; set; }

        IGasPriceOracle? GasPriceOracle { get; set; }

        IEthSyncingInfo? EthSyncingInfo { get; set; }

        CompositePruningTrigger PruningTrigger { get; }

        IBlockProductionPolicy? BlockProductionPolicy { get; set; }

        ByPathStateDbPrunner? ByPathDbPrunnerState { get; set; }
        ByPathStateDbPrunner? ByPathDbPrunnerStorage { get; set; }
    }
}
