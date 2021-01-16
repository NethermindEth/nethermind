//  Copyright (c) 2018 Demerzel Solutions Limited
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

using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Processing;
using Nethermind.Blockchain.Producers;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Rewards;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.TxPool;

namespace Nethermind.Consensus.Ethash
{
    public class NethDevPlugin : IConsensusPlugin
    {
        private INethermindApi _nethermindApi;
        public void Dispose() { }

        public string Name => "NethDev";

        public string Description => "NethDev (Spaceneth)";

        public string Author => "Nethermind";
        
        public Task Init(INethermindApi nethermindApi)
        {
            _nethermindApi = nethermindApi;
            return Task.CompletedTask;
        }

        public Task InitBlockProducer()
        {
            if (_nethermindApi!.SealEngineType != SealEngineType.NethDev)
            {
                return Task.CompletedTask;
            }
            
            var (getFromApi, setInApi) = _nethermindApi!.ForProducer;
            ITxFilter txFilter = new NullTxFilter();
            ITxSource txSource = new TxPoolTxSource(
                getFromApi.TxPool, getFromApi.StateReader, getFromApi.LogManager, txFilter);
            
            ILogger logger = getFromApi.LogManager.GetClassLogger();
            if (logger.IsWarn) logger.Warn("Starting Neth Dev block producer & sealer");
            
            ReadOnlyDbProvider readOnlyDbProvider = new ReadOnlyDbProvider(getFromApi.DbProvider, false);
            ReadOnlyBlockTree readOnlyBlockTree = new ReadOnlyBlockTree(getFromApi.BlockTree);

            ReadOnlyTxProcessingEnv producerEnv = new ReadOnlyTxProcessingEnv(
                readOnlyDbProvider,
                getFromApi.ReadOnlyTrieStore,
                readOnlyBlockTree,
                getFromApi.SpecProvider,
                getFromApi.LogManager);

            BlockProcessor producerProcessor = new BlockProcessor(
                getFromApi!.SpecProvider,
                getFromApi!.BlockValidator,
                NoBlockRewards.Instance,
                producerEnv.TransactionProcessor,
                producerEnv.StateProvider,
                producerEnv.StorageProvider,
                NullTxPool.Instance,
                NullReceiptStorage.Instance,
                getFromApi.WitnessCollector,
                getFromApi.LogManager);

            IBlockchainProcessor producerChainProcessor = new BlockchainProcessor(
                readOnlyBlockTree,
                producerProcessor,
                getFromApi.BlockPreprocessor,
                getFromApi.LogManager,
                BlockchainProcessor.Options.NoReceipts);
            
            setInApi.BlockProducer = new DevBlockProducer(
                txSource,
                producerChainProcessor,
                producerEnv.StateProvider,
                getFromApi.BlockTree,
                getFromApi.BlockProcessingQueue,
                getFromApi.TxPool,
                getFromApi.Timestamper,
                getFromApi.SpecProvider,
                getFromApi.Config<IMiningConfig>(),
                getFromApi.LogManager);
                
            return Task.CompletedTask;
        }

        public SealEngineType SealEngineType => SealEngineType.NethDev;

        public Task InitNetworkProtocol()
        {
            return Task.CompletedTask;
        }

        public Task InitRpcModules()
        {
            return Task.CompletedTask;
        }
    }
}
