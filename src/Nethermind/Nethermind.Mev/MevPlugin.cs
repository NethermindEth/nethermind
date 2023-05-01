// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;
using Nethermind.Mev.Data;
using Nethermind.Mev.Execution;
using Nethermind.Mev.Source;

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

        public BundlePool BundlePool
        {
            get
            {
                if (_bundlePool is null)
                {
                    var (getFromApi, _) = _nethermindApi!.ForProducer;

                    TxBundleSimulator txBundleSimulator = new(
                        TracerFactory,
                        getFromApi.GasLimitCalculator!,
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
                        getFromApi.ChainHeadStateProvider!,
                        getFromApi.LogManager,
                        getFromApi.EthereumEcdsa!);
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

                MevModuleFactory mevModuleFactory = new(rpcConfig,
                    BundlePool,
                    getFromApi.BlockTree!,
                    getFromApi.StateReader!,
                    TracerFactory,
                    getFromApi.SpecProvider!,
                    getFromApi.EngineSigner);

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

            _nethermindApi.BlockProducerEnvFactory!.TransactionsExecutorFactory = new MevBlockProducerTransactionsExecutorFactory(_nethermindApi.SpecProvider!, _nethermindApi.LogManager);

            int megabundleProducerCount = _mevConfig.GetTrustedRelayAddresses().Any() ? 1 : 0;
            List<MevBlockProducer.MevBlockProducerInfo> blockProducers =
                new(_mevConfig.MaxMergedBundles + megabundleProducerCount + 1);

            // Add non-mev block
            MevBlockProducer.MevBlockProducerInfo standardProducer = await CreateProducer(consensusPlugin);
            blockProducers.Add(standardProducer);

            // Try blocks with all bundle numbers <= MaxMergedBundles
            for (int bundleLimit = 1; bundleLimit <= _mevConfig.MaxMergedBundles; bundleLimit++)
            {
                BundleSelector bundleSelector = new(BundlePool, bundleLimit);
                MevBlockProducer.MevBlockProducerInfo bundleProducer = await CreateProducer(consensusPlugin, bundleLimit, new BundleTxSource(bundleSelector, _nethermindApi.Timestamper));
                blockProducers.Add(bundleProducer);
            }

            if (megabundleProducerCount > 0)
            {
                MegabundleSelector megabundleSelector = new(BundlePool);
                MevBlockProducer.MevBlockProducerInfo bundleProducer = await CreateProducer(consensusPlugin, 0, new BundleTxSource(megabundleSelector, _nethermindApi.Timestamper));
                blockProducers.Add(bundleProducer);
            }

            return new MevBlockProducer(consensusPlugin.DefaultBlockProductionTrigger, _nethermindApi.LogManager, blockProducers.ToArray());
        }

        private async Task<MevBlockProducer.MevBlockProducerInfo> CreateProducer(
            IConsensusPlugin consensusPlugin,
            int bundleLimit = 0,
            ITxSource? additionalTxSource = null)
        {
            bool BundleLimitTriggerCondition(BlockProductionEventArgs e)
            {
                // TODO: why we are checking parent and not the currently produced block...?
                BlockHeader? parent = _nethermindApi.BlockTree!.GetProducedBlockParent(e.ParentHeader);
                if (parent is not null)
                {
                    IEnumerable<MevBundle> bundles = BundlePool.GetBundles(parent, _nethermindApi.Timestamper);
                    return bundles.Count() >= bundleLimit;
                }

                return false;
            }

            IManualBlockProductionTrigger manualTrigger = new BuildBlocksWhenRequested();
            IBlockProductionTrigger trigger = manualTrigger;
            if (bundleLimit != 0)
            {
                trigger = new TriggerWithCondition(manualTrigger, BundleLimitTriggerCondition);
            }

            IBlockProducer producer = await consensusPlugin.InitBlockProducer(trigger, additionalTxSource);
            return new MevBlockProducer.MevBlockProducerInfo(producer, manualTrigger, new BeneficiaryTracer());
        }

        public bool Enabled => _mevConfig.Enabled;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
