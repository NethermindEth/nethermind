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
using System.Collections.Concurrent;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Linq;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Processing;
using Nethermind.Blockchain.Producers;
using Nethermind.Consensus;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Facade;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;
using Nethermind.Mev.Data;
using Nethermind.Mev.Execution;
using Nethermind.Mev.Source;
using Nethermind.TxPool;

namespace Nethermind.Mev
{
    public class MevPlugin : IConsensusWrapperPlugin
    {
        private static readonly ProcessingOptions SimulateBundleProcessingOptions = ProcessingOptions.ProducingBlock | ProcessingOptions.IgnoreParentNotOnMainChain;
        
        private IMevConfig _mevConfig = null!;
        private ILogger? _logger;
        private INethermindApi _nethermindApi = null!;
        private BundlePool? _bundlePool;
        private ITracerFactory? _tracerFactory;
        
        public string Name => "MEV";

        public string Description => "Flashbots MEV spec implementation";

        public string Author => "Nethermind";

        public Task Init(INethermindApi? nethermindApi)
        {
            _nethermindApi = nethermindApi ?? throw new ArgumentNullException(nameof(nethermindApi));
            _mevConfig = _nethermindApi.Config<IMevConfig>();
            _logger = _nethermindApi.LogManager.GetClassLogger();

            return Task.CompletedTask;
        }

        public Task InitNetworkProtocol() => Task.CompletedTask;

        private BundlePool BundlePool
        {
            get
            {
                if (_bundlePool is null)
                {
                    var (getFromApi, _) = _nethermindApi!.ForProducer;
                    
                    TxBundleSimulator txBundleSimulator = new(
                        TracerFactory, 
                        getFromApi.GasLimitCalculator,
                        getFromApi.Timestamper,
                        getFromApi.TxPool!, 
                        getFromApi.SpecProvider!, 
                        getFromApi.EngineSigner);
                    
                    _bundlePool = new BundlePool(
                        getFromApi.BlockTree!, 
                        txBundleSimulator, 
                        getFromApi.Timestamper,
                        getFromApi.TxValidator!,
                        getFromApi.SpecProvider!,
                        _mevConfig,
                        getFromApi.LogManager);
                }

                return _bundlePool;
            }
        }

        private ITracerFactory TracerFactory
        {
            get
            {
                if (_tracerFactory is null)
                {
                    var (getFromApi, _) = _nethermindApi!.ForProducer;
                    
                    _tracerFactory = new TracerFactory(
                        getFromApi.DbProvider!,
                        getFromApi.BlockTree!,
                        getFromApi.ReadOnlyTrieStore!,
                        getFromApi.BlockPreprocessor!,
                        getFromApi.SpecProvider!,
                        getFromApi.LogManager!,
                        SimulateBundleProcessingOptions);
                }

                return _tracerFactory;
            }
        }

        public Task InitRpcModules()
        {
            if (_mevConfig.Enabled) 
            {   
                (IApiWithNetwork getFromApi, _) = _nethermindApi!.ForRpc;

                IJsonRpcConfig rpcConfig = getFromApi.Config<IJsonRpcConfig>();
                rpcConfig.EnableModules(ModuleType.Mev);

                MevModuleFactory mevModuleFactory = new(
                    _mevConfig!, 
                    rpcConfig, 
                    BundlePool, 
                    getFromApi.BlockTree!,
                    getFromApi.StateReader!,
                    TracerFactory,
                    getFromApi.SpecProvider!,
                    getFromApi.EngineSigner,
                    getFromApi.ChainSpec!.ChainId);
                
                getFromApi.RpcModuleProvider!.RegisterBoundedByCpuCount(mevModuleFactory, rpcConfig.Timeout);

                if (_logger!.IsInfo) _logger.Info("Flashbots RPC plugin enabled");
            } 
            else 
            {
                if (_logger!.IsWarn) _logger.Info("Skipping Flashbots RPC plugin");
            }

            return Task.CompletedTask;
        }

        public async Task<IBlockProducer> InitBlockProducer(IConsensusPlugin consensusPlugin)
        {
            if (!Enabled)
            {
                throw new InvalidOperationException("Plugin is disabled");
            }

            _nethermindApi.BlockProducerEnvFactory.TransactionsExecutorFactory = new MevBlockProducerTransactionsExecutorFactory(_nethermindApi.SpecProvider!, _nethermindApi.LogManager);
            
            List<MevBlockProducer.MevBlockProducerInfo> blockProducers =
                new(_mevConfig.MaxMergedBundles + 1);
                
            // Add non-mev block
            MevBlockProducer.MevBlockProducerInfo standardProducer = await CreateProducer(consensusPlugin);
            blockProducers.Add(standardProducer);
            
            // Try blocks with all bundle numbers <= MaxMergedBundles
            for (int bundleLimit = 1; bundleLimit <= _mevConfig.MaxMergedBundles; bundleLimit++)
            {
                BundleSelector bundleSelector = new(BundlePool, bundleLimit);
                MevBlockProducer.MevBlockProducerInfo bundleProducer = await CreateProducer(consensusPlugin, new BundleTxSource(bundleSelector, _nethermindApi.Timestamper));
                blockProducers.Add(bundleProducer);
            }

            return new MevBlockProducer(consensusPlugin.DefaultBlockProductionTrigger, _nethermindApi.LogManager, blockProducers.ToArray());
        }

        private static async Task<MevBlockProducer.MevBlockProducerInfo> CreateProducer(
            IConsensusPlugin consensusPlugin,
            ITxSource? additionalTxSource = null)
        {
            IManualBlockProductionTrigger trigger = new BuildBlocksWhenRequested();
            IBlockProducer producer = await consensusPlugin.InitBlockProducer(trigger, additionalTxSource);
            return new MevBlockProducer.MevBlockProducerInfo(producer, trigger, new BeneficiaryTracer());
        }

        public bool Enabled => _mevConfig.Enabled;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
