//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

#nullable enable
using Nethermind.Blockchain;
using Nethermind.Blockchain.Comparers;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Processing;
using Nethermind.Blockchain.Producers;
using Nethermind.Blockchain.Rewards;
using Nethermind.Blockchain.Validators;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Facade;
using Nethermind.State;
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
        IHeaderValidator? HeaderValidator { get; set; }
        IManualBlockProductionTrigger ManualBlockProductionTrigger { get; set; }
        IReadOnlyTrieStore? ReadOnlyTrieStore { get; set; }
        IRewardCalculatorSource? RewardCalculatorSource { get; set; }
        ISealer? Sealer { get; set; }
        ISealValidator? SealValidator { get; set; }
        
        /// <summary>
        /// Can be used only for processing blocks, on all other contexts use <see cref="StateReader"/> or <see cref="ChainHeadStateProvider"/>.
        /// </summary>
        /// <remarks>
        /// DO NOT USE OUTSIDE OF PROCESSING BLOCK CONTEXT!
        /// </remarks>
        IStateProvider? StateProvider { get; set; }
        IKeyValueStoreWithBatching? MainStateDbWithCache { get; set; }
        IReadOnlyStateProvider? ChainHeadStateProvider { get; set; }
        IStateReader? StateReader { get; set; }
        IStorageProvider? StorageProvider { get; set; }
        ITransactionProcessor? TransactionProcessor { get; set; }
        ITrieStore? TrieStore { get; set; }
        ITxSender? TxSender { get; set; }
        ITxPool? TxPool { get; set; }
        ITxPoolInfoProvider? TxPoolInfoProvider { get; set; }
        IWitnessCollector? WitnessCollector { get; set; }
        IWitnessRepository? WitnessRepository { get; set; }
        IHealthHintService? HealthHintService { get; set; }
        ITransactionComparerProvider? TransactionComparerProvider { get; set; }
        TxValidator? TxValidator { get; set; }
        
        /// <summary>
        /// Manager of block finalization
        /// </summary>
        /// <remarks>
        /// Currently supported in <see cref="SealEngineType.AuRa"/> and Eth2Merge.
        /// </remarks>
        IBlockFinalizationManager? FinalizationManager { get; set; }
        
        IGasLimitCalculator GasLimitCalculator { get; set; }
        
        IBlockProducerEnvFactory BlockProducerEnvFactory { get; set; }
    }
}
