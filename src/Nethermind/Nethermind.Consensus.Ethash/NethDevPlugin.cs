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

using System;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Comparers;
using Nethermind.Blockchain.Processing;
using Nethermind.Blockchain.Producers;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Rewards;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.TxPool;

namespace Nethermind.Consensus.Ethash
{
    public class NethDevPlugin : IConsensusPlugin
    {
        private INethermindApi? _nethermindApi;

        public ValueTask DisposeAsync() { return ValueTask.CompletedTask; }

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
            if (_nethermindApi!.SealEngineType != Nethermind.Core.SealEngineType.NethDev)
            {
                return Task.CompletedTask;
            }
            var (getFromApi, setInApi) = _nethermindApi!.ForProducer;

            ReadOnlyDbProvider readOnlyDbProvider = getFromApi.DbProvider.AsReadOnly(false);
            ReadOnlyBlockTree readOnlyBlockTree = getFromApi.BlockTree.AsReadOnly();

            ITransactionComparerProvider transactionComparerProvider =
                new TransactionComparerProvider(getFromApi.SpecProvider, readOnlyBlockTree);
            IBlockPreparationContextService blockPreparationContextService = new BlockPreparationContextService(_nethermindApi.LogManager);
            ITxFilterPipeline txFilterPipeline = new TxFilterPipelineBuilder(_nethermindApi.LogManager)
                .WithBaseFeeFilter(blockPreparationContextService, getFromApi.SpecProvider)
                .WithNullTxFilter()
                .Build;
            
            ITxSource txSource = new TxPoolTxSource(
                getFromApi.TxPool, 
                getFromApi.StateReader,
                getFromApi.SpecProvider,
                transactionComparerProvider.GetDefaultProducerComparer(blockPreparationContextService),
                blockPreparationContextService,
                getFromApi.LogManager,
                txFilterPipeline);
            
            ILogger logger = getFromApi.LogManager.GetClassLogger();
            if (logger.IsWarn) logger.Warn("Starting Neth Dev block producer & sealer");


            ReadOnlyTxProcessingEnv producerEnv = new(
                readOnlyDbProvider,
                getFromApi.ReadOnlyTrieStore,
                readOnlyBlockTree,
                getFromApi.SpecProvider,
                getFromApi.LogManager);

            BlockProcessor producerProcessor = new(
                getFromApi!.SpecProvider,
                getFromApi!.BlockValidator,
                NoBlockRewards.Instance,
                producerEnv.TransactionProcessor,
                producerEnv.StateProvider,
                producerEnv.StorageProvider,
                NullTxPool.Instance,
                NullReceiptStorage.Instance,
                NullWitnessCollector.Instance,
                getFromApi.LogManager);

            IBlockchainProcessor producerChainProcessor = new BlockchainProcessor(
                readOnlyBlockTree,
                producerProcessor,
                getFromApi.BlockPreprocessor,
                getFromApi.LogManager,
                BlockchainProcessor.Options.NoReceipts);
            
            setInApi.BlockProducer = new DevBlockProducer(
                txSource.ServeTxsOneByOne(),
                producerChainProcessor,
                producerEnv.StateProvider,
                getFromApi.BlockTree,
                getFromApi.BlockProcessingQueue,
                new BuildBlocksRegularly(TimeSpan.FromMilliseconds(200))
                    .IfPoolIsNotEmpty(getFromApi.TxPool)
                    .Or(getFromApi.ManualBlockProductionTrigger),
                getFromApi.Timestamper,
                getFromApi.SpecProvider,
                getFromApi.Config<IMiningConfig>(),
                blockPreparationContextService,
                getFromApi.LogManager);

            return Task.CompletedTask;
        }

        public string SealEngineType => Nethermind.Core.SealEngineType.NethDev;

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
