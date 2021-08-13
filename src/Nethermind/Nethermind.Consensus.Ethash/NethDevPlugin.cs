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
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;

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

        public Task<IBlockProducer> InitBlockProducer(IBlockProductionTrigger? blockProductionTrigger = null, ITxSource? additionalTxSource = null)
        {
            if (_nethermindApi!.SealEngineType != Nethermind.Core.SealEngineType.NethDev)
            {
                return Task.FromResult((IBlockProducer)null);
            }
            var (getFromApi, setInApi) = _nethermindApi!.ForProducer;

            ReadOnlyDbProvider readOnlyDbProvider = getFromApi.DbProvider.AsReadOnly(false);
            ReadOnlyBlockTree readOnlyBlockTree = getFromApi.BlockTree.AsReadOnly();
            
            ITxFilterPipeline txFilterPipeline = new TxFilterPipelineBuilder(_nethermindApi.LogManager)
                .WithBaseFeeFilter(getFromApi.SpecProvider)
                .WithNullTxFilter()
                .WithMinGasPriceFilter(_nethermindApi.Config<IMiningConfig>().MinGasPrice, getFromApi.SpecProvider)
                .Build;

            TxPoolTxSource txPoolTxSource = new(
                getFromApi.TxPool,
                getFromApi.SpecProvider,
                getFromApi.TransactionComparerProvider!,
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
                new BlockProcessor.BlockProductionTransactionsExecutor(producerEnv, getFromApi!.SpecProvider, getFromApi.LogManager),
                producerEnv.StateProvider,
                producerEnv.StorageProvider,
                NullReceiptStorage.Instance,
                NullWitnessCollector.Instance,
                getFromApi.LogManager);

            IBlockchainProcessor producerChainProcessor = new BlockchainProcessor(
                readOnlyBlockTree,
                producerProcessor,
                getFromApi.BlockPreprocessor,
                getFromApi.LogManager,
                BlockchainProcessor.Options.NoReceipts);

            DefaultBlockProductionTrigger = new BuildBlocksRegularly(TimeSpan.FromMilliseconds(200))
                .IfPoolIsNotEmpty(getFromApi.TxPool)
                .Or(getFromApi.ManualBlockProductionTrigger);
            
            IBlockProducer blockProducer = new DevBlockProducer(
                additionalTxSource.Then(txPoolTxSource).ServeTxsOneByOne(),
                producerChainProcessor,
                producerEnv.StateProvider,
                getFromApi.BlockTree,
                blockProductionTrigger ?? DefaultBlockProductionTrigger,
                getFromApi.Timestamper,
                getFromApi.SpecProvider,
                getFromApi.Config<IMiningConfig>(),
                getFromApi.LogManager);

            return Task.FromResult(blockProducer);
        }

        public string SealEngineType => Nethermind.Core.SealEngineType.NethDev;
        public IBlockProductionTrigger DefaultBlockProductionTrigger { get; private set; }

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
