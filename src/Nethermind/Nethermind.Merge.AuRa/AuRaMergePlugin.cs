// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Consensus.AuRa.InitializationSteps;
using Nethermind.Consensus.AuRa.Transactions;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Merge.AuRa.Shutter;
using Nethermind.Merge.Plugin;
using Nethermind.Merge.Plugin.BlockProduction;
using Nethermind.Consensus.Processing;
using Multiformats.Address;
using Nethermind.Serialization.Json;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Merge.AuRa.Shutter.Contracts;
using Nethermind.Logging;
using Microsoft.IdentityModel.Tokens;
using Nethermind.Blockchain.Receipts;
using Nethermind.Facade.Proxy;
using Nethermind.HealthChecks;
using Nethermind.Merge.Plugin.BlockProduction.Boost;
using Nethermind.Merge.Plugin.GC;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Merge.Plugin.Synchronization;
using System.Net.Http;
using System.Threading;

namespace Nethermind.Merge.AuRa
{
    /// <summary>
    /// Plugin for AuRa -> PoS migration
    /// </summary>
    /// <remarks>IMPORTANT: this plugin should always come before MergePlugin</remarks>
    public class AuRaMergePlugin : MergePlugin, IInitializationPlugin
    {
        private AuRaNethermindApi? _auraApi;
        private IShutterConfig? _shutterConfig;
        private ShutterP2P? _shutterP2P;
        private EventHandler<BlockEventArgs>? _eonUpdateHandler;

        public override string Name => "AuRaMerge";
        public override string Description => "AuRa Merge plugin for ETH1-ETH2";
        protected override bool MergeEnabled => ShouldRunSteps(_api);

        public class ShutterLoadingException(string message, Exception? innerException = null) : Exception(message, innerException);

        public override async Task Init(INethermindApi nethermindApi)
        {
            _api = nethermindApi;
            _mergeConfig = _api.Config<IMergeConfig>();
            _shutterConfig = _api.Config<IShutterConfig>();

            if (MergeEnabled)
            {
                await base.Init(_api);
                _auraApi = (AuRaNethermindApi)_api;
                _auraApi.PoSSwitcher = _poSSwitcher;

                // this runs before all init steps that use tx filters
                TxAuRaFilterBuilders.CreateFilter = (originalFilter, fallbackFilter) =>
                    originalFilter is MinGasPriceContractTxFilter ? originalFilter
                    : new AuRaMergeTxFilter(_poSSwitcher, originalFilter, fallbackFilter);
            }
        }

        public override IBlockProducer InitBlockProducer(IBlockProducerFactory consensusPlugin, ITxSource? txSource)
        {
            _api.BlockProducerEnvFactory = new AuRaMergeBlockProducerEnvFactory(
                _auraApi!,
                _api.WorldStateManager!,
                _api.BlockTree!,
                _api.SpecProvider!,
                _api.BlockValidator!,
                _api.RewardCalculatorSource!,
                _api.ReceiptStorage!,
                _api.BlockPreprocessor!,
                _api.TxPool!,
                _api.TransactionComparerProvider!,
                _api.Config<IBlocksConfig>(),
                _api.LogManager);

            IBlockProducer blockProducer = base.InitBlockProducer(consensusPlugin, txSource);

            return blockProducer;
        }

        public new Task InitRpcModules()
        {
            IBlockImprovementContextFactory? blockImprovementContextFactory = null;
            if (_shutterConfig!.Enabled)
            {
                blockImprovementContextFactory = new ShutterBlockImprovementContextFactory(
                    _api.BlockProducer!,
                    _shutterTxSource!,
                    _shutterConfig,
                    _api.SpecProvider!,
                    _api.LogManager);
            }
            base.InitRpcModulesInternal(blockImprovementContextFactory);
            return Task.CompletedTask;
        }

        protected override PostMergeBlockProducerFactory CreateBlockProducerFactory()
            => new AuRaPostMergeBlockProducerFactory(
                _api.SpecProvider!,
                _api.SealEngine,
                _manualTimestamper!,
                _blocksConfig,
                _api.LogManager);

        ShutterTxSource? _shutterTxSource = null;

        protected override BlockProducerEnv CreateBlockProducerEnv()
        {
            Debug.Assert(_api?.BlockProducerEnvFactory is not null,
                $"{nameof(_api.BlockProducerEnvFactory)} has not been initialized.");

            Logging.ILogger logger = _api.LogManager.GetClassLogger();

            if (_shutterConfig!.Enabled)
            {
                ValidateShutterConfig(_shutterConfig);

                Dictionary<ulong, byte[]> validatorsInfo = [];
                if (_shutterConfig.ValidatorInfoFile is not null)
                {
                    try
                    {
                        validatorsInfo = LoadValidatorInfo(_shutterConfig.ValidatorInfoFile);
                    }
                    catch (Exception e)
                    {
                        throw new ShutterLoadingException("Could not load Shutter validator info file", e);
                    }
                }

                IReadOnlyBlockTree readOnlyBlockTree = _api.BlockTree!.AsReadOnly();
                ReadOnlyTxProcessingEnvFactory readOnlyTxProcessingEnvFactory = new(_api.WorldStateManager!, readOnlyBlockTree, _api.SpecProvider, _api.LogManager);

                ShutterEon shutterEon = new(readOnlyBlockTree, readOnlyTxProcessingEnvFactory, _api.AbiEncoder!, _shutterConfig, logger);
                bool haveCheckedRegistered = false;
                _eonUpdateHandler = (_, e) =>
                {
                    int headerAge = (int)(e.Block.Header.Timestamp - (ulong)DateTimeOffset.Now.ToUnixTimeSeconds());
                    if (headerAge < 10)
                    {
                        if (!haveCheckedRegistered)
                        {
                            CheckRegistered(e.Block.Header, validatorsInfo, readOnlyTxProcessingEnvFactory, logger);
                            haveCheckedRegistered = true;
                        }
                        shutterEon.Update(e.Block.Header);
                    }
                };
                _api.BlockTree!.NewHeadBlock += _eonUpdateHandler;

                _shutterTxSource = new ShutterTxSource(new ShutterTxLoader(
                    _api.LogFinder!,
                    _shutterConfig,
                    _api.SpecProvider!,
                    _api.EthereumEcdsa!,
                    readOnlyBlockTree,
                    _api.LogManager), _shutterConfig, _api.SpecProvider!, _api.LogManager);

                ShutterMessageHandler shutterMessageHandler = new(_shutterConfig, _shutterTxSource, shutterEon, _api.LogManager);
                _shutterP2P = new(shutterMessageHandler.OnDecryptionKeysReceived, _shutterConfig, _api.LogManager);
                _shutterP2P.Start(_shutterConfig.KeyperP2PAddresses);
            }

            return _api.BlockProducerEnvFactory.Create(_shutterTxSource);
        }

        public bool ShouldRunSteps(INethermindApi api)
        {
            _mergeConfig = api.Config<IMergeConfig>();
            return _mergeConfig.Enabled && api.ChainSpec.SealEngineType == SealEngineType.AuRa;
        }

        public override async ValueTask DisposeAsync()
        {
            if (_eonUpdateHandler is not null)
            {
                _api.BlockTree!.NewHeadBlock -= _eonUpdateHandler;
            }
            await (_shutterP2P?.DisposeAsync() ?? default);
            await base.DisposeAsync();
        }

        private void CheckRegistered(BlockHeader parent, Dictionary<ulong, byte[]> validatorsInfo, ReadOnlyTxProcessingEnvFactory envFactory, ILogger logger)
        {
            if (validatorsInfo.IsNullOrEmpty())
            {
                return;
            }

            IReadOnlyTxProcessingScope scope = envFactory.Create().Build(parent.StateRoot!);
            ITransactionProcessor processor = scope.TransactionProcessor;

            ValidatorRegistryContract validatorRegistryContract = new(processor, _api.AbiEncoder!, new(_shutterConfig!.ValidatorRegistryContractAddress!), logger, _api.SpecProvider!.ChainId, _shutterConfig.ValidatorRegistryMessageVersion);
            if (validatorRegistryContract.IsRegistered(parent, validatorsInfo, out HashSet<ulong> unregistered))
            {
                if (logger.IsInfo) logger.Info($"All Shutter validators are registered.");
            }
            else
            {
                if (logger.IsError) logger.Error($"Validators not registered to Shutter with the following indices: [{string.Join(", ", unregistered)}]");
            }
        }

        private void ValidateShutterConfig(IShutterConfig shutterConfig)
        {
            if (shutterConfig.Validator && shutterConfig.ValidatorInfoFile is null)
            {
                throw new ArgumentException($"Must set Shutter.ValidatorInfoFile to a valid json file.");
            }

            if (shutterConfig.ValidatorInfoFile is not null && !File.Exists(shutterConfig.ValidatorInfoFile))
            {
                throw new ArgumentException($"Shutter validator info file \"{shutterConfig.ValidatorInfoFile}\" does not exist.");
            }

            if (shutterConfig.SequencerContractAddress is null || !Address.TryParse(shutterConfig.SequencerContractAddress, out _))
            {
                throw new ArgumentException("Must set Shutter sequencer contract address to valid address.");
            }

            if (shutterConfig.ValidatorRegistryContractAddress is null || !Address.TryParse(shutterConfig.ValidatorRegistryContractAddress, out _))
            {
                throw new ArgumentException("Must set Shutter validator registry contract address to valid address.");
            }

            if (shutterConfig.KeyBroadcastContractAddress is null || !Address.TryParse(shutterConfig.KeyBroadcastContractAddress, out _))
            {
                throw new ArgumentException("Must set Shutter key broadcast contract address to valid address.");
            }

            if (shutterConfig.KeyperSetManagerContractAddress is null || !Address.TryParse(shutterConfig.KeyperSetManagerContractAddress, out _))
            {
                throw new ArgumentException("Must set Shutter keyper set manager contract address to valid address.");
            }

            foreach (string addr in shutterConfig.KeyperP2PAddresses)
            {
                try
                {
                    Multiaddress.Decode(addr);
                }
                catch (NotSupportedException)
                {
                    throw new ArgumentException($"Could not decode Shutter keyper p2p address \"{addr}\".");
                }
            }
        }

        private Dictionary<ulong, byte[]> LoadValidatorInfo(string fp)
        {
            FileStream fstream = new FileStream(fp, FileMode.Open, FileAccess.Read, FileShare.None);
            return new EthereumJsonSerializer().Deserialize<Dictionary<ulong, byte[]>>(fstream);
        }
    }
}
