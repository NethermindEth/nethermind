// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Shutter.Config;
using Nethermind.Merge.Plugin;
using Nethermind.Consensus.Processing;
using Nethermind.Logging;
using Nethermind.Specs;

namespace Nethermind.Shutter
{
    public class ShutterPlugin : IConsensusWrapperPlugin, IInitializationPlugin
    {
        public string Name => "Shutter";
        public string Description => "Shutter plugin for AuRa post-merge chains";
        public string Author => "Nethermind";
        public bool Enabled => ShouldRunSteps(_api!);
        public int Priority => PluginPriorities.Shutter;

        private INethermindApi? _api;
        private IMergeConfig? _mergeConfig;
        private IShutterConfig? _shutterConfig;
        private ShutterP2P? _shutterP2P;
        private EventHandler<BlockEventArgs>? _newHeadBlockHandler;
        private EventHandler<IShutterMessageHandler.ValidatedKeyArgs>? _keysValidatedHandler;
        private ShutterTxSource? _txSource;
        private IShutterMessageHandler? _msgHandler;
        private ILogger _logger;
        private readonly TimeSpan _slotLength = GnosisSpecProvider.SlotLength;
        private readonly TimeSpan _blockWaitCutoff = TimeSpan.FromMilliseconds(1333);

        public class ShutterLoadingException(string message, Exception? innerException = null) : Exception(message, innerException);

        public Task Init(INethermindApi nethermindApi)
        {
            _logger.Info($"Initializing Shutter plugin.");
            _api = nethermindApi;
            _mergeConfig = _api.Config<IMergeConfig>();
            _shutterConfig = _api.Config<IShutterConfig>();
            _logger = _api.LogManager.GetClassLogger();
            return Task.CompletedTask;
        }

        public Task InitRpcModules()
        {
            if (Enabled)
            {
                if (_api!.BlockProducer is null) throw new ArgumentNullException(nameof(_api.BlockProducer));
                if (_api.SpecProvider is null) throw new ArgumentNullException(nameof(_api.SpecProvider));
                if (_api.LogManager is null) throw new ArgumentNullException(nameof(_api.LogManager));
                if (_txSource is null) throw new ArgumentNullException(nameof(_txSource));
                if (_shutterConfig is null) throw new ArgumentNullException(nameof(_shutterConfig));

                _logger.Info("Initializing Shutter block improvement.");
                _api.BlockImprovementContextFactory = new ShutterBlockImprovementContextFactory(
                    _api.BlockProducer,
                    _txSource,
                    _shutterConfig,
                    _api.SpecProvider,
                    _api.LogManager);
            }
            return Task.CompletedTask;
        }


        public IBlockProducer InitBlockProducer(IBlockProducerFactory consensusPlugin, ITxSource? txSource)
        {
            if (Enabled)
            {
                if (_api!.AbiEncoder is null) throw new ArgumentNullException(nameof(_api.AbiEncoder));
                if (_api.BlockTree is null) throw new ArgumentNullException(nameof(_api.BlockTree));
                if (_api.BlockProducerEnvFactory is null) throw new ArgumentNullException(nameof(_api.BlockProducerEnvFactory));
                if (_api.EthereumEcdsa is null) throw new ArgumentNullException(nameof(_api.SpecProvider));
                if (_api.LogFinder is null) throw new ArgumentNullException(nameof(_api.LogFinder));
                if (_api.LogManager is null) throw new ArgumentNullException(nameof(_api.LogManager));
                if (_api.SpecProvider is null) throw new ArgumentNullException(nameof(_api.SpecProvider));
                if (_api.WorldStateManager is null) throw new ArgumentNullException(nameof(_api.WorldStateManager));

                _logger.Info("Initializing Shutter block producer.");

                ShutterHelpers.ValidateConfig(_shutterConfig!);

                Dictionary<ulong, byte[]> validatorsInfo = [];
                if (_shutterConfig!.ValidatorInfoFile is not null)
                {
                    try
                    {
                        validatorsInfo = ShutterHelpers.LoadValidatorInfo(_shutterConfig!.ValidatorInfoFile);
                    }
                    catch (Exception e)
                    {
                        throw new ShutterLoadingException("Could not load Shutter validator info file", e);
                    }
                }

                IReadOnlyBlockTree readOnlyBlockTree = _api.BlockTree!.AsReadOnly();
                ReadOnlyTxProcessingEnvFactory readOnlyTxProcessingEnvFactory = new(_api.WorldStateManager!, readOnlyBlockTree, _api.SpecProvider, _api.LogManager);

                ShutterTxLoader txLoader = new(_api.LogFinder!, _shutterConfig, _api.SpecProvider!, _api.EthereumEcdsa!, _api.LogManager);
                ShutterEon eon = new(readOnlyBlockTree, readOnlyTxProcessingEnvFactory, _api.AbiEncoder!, _shutterConfig, _logger);
                ShutterBlockHandler blockHandler = new(
                    _api.SpecProvider!.ChainId,
                    _shutterConfig.ValidatorRegistryContractAddress!,
                    _shutterConfig.ValidatorRegistryMessageVersion,
                    readOnlyTxProcessingEnvFactory,
                    _api.AbiEncoder, _api.ReceiptFinder!, _api.SpecProvider!,
                    validatorsInfo, eon, txLoader, _api.LogManager);

                _newHeadBlockHandler = (_, e) =>
                {
                    blockHandler.OnNewHeadBlock(e.Block);
                };
                _api.BlockTree!.NewHeadBlock += _newHeadBlockHandler;

                _txSource = new ShutterTxSource(txLoader, _shutterConfig, _api.SpecProvider!, _api.LogManager);

                _msgHandler = new ShutterMessageHandler(_shutterConfig, eon, _api.LogManager);
                _keysValidatedHandler = async (_, keys) =>
                {
                    // wait for latest block before loading transactions
                    Block? head = await blockHandler.WaitForBlockInSlot(keys.Slot - 1, _slotLength, _blockWaitCutoff, new());
                    _txSource.LoadTransactions(head is null ? _api.BlockTree.Head : head, keys);
                };
                _msgHandler.KeysValidated += _keysValidatedHandler;

                _shutterP2P = new(_msgHandler.OnDecryptionKeysReceived, _shutterConfig, _api.LogManager);
                _shutterP2P.Start(_shutterConfig.KeyperP2PAddresses!);
            }

            return consensusPlugin.InitBlockProducer(_txSource.Then(txSource));
        }

        public bool ShouldRunSteps(INethermindApi api)
        {
            _shutterConfig = api.Config<IShutterConfig>();
            _mergeConfig = api.Config<IMergeConfig>();
            return _shutterConfig!.Enabled && _mergeConfig!.Enabled && api.ChainSpec.SealEngineType is SealEngineType.AuRa;
        }

        public async ValueTask DisposeAsync()
        {
            if (_newHeadBlockHandler is not null)
            {
                _api!.BlockTree!.NewHeadBlock -= _newHeadBlockHandler;
            }
            if (_keysValidatedHandler is not null)
            {
                _msgHandler!.KeysValidated -= _keysValidatedHandler;
            }
            await (_shutterP2P?.DisposeAsync() ?? default);
        }
    }
}
