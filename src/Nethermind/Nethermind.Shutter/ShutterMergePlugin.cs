// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Core;
using Nethermind.Shutter;
using Nethermind.Merge.AuRa;
using Nethermind.Merge.Plugin.BlockProduction;
using Nethermind.Consensus.Processing;
using Multiformats.Address;
using Nethermind.Serialization.Json;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Shutter.Contracts;
using Nethermind.Logging;

namespace Nethermind.Shutter
{
    /// <summary>
    /// Plugin for AuRa -> PoS migration
    /// </summary>
    /// <remarks>IMPORTANT: this plugin should always come before MergePlugin</remarks>
    public class ShutterMergePlugin : AuRaMergePlugin
    {
        private IShutterConfig? _shutterConfig;
        private ShutterP2P? _shutterP2P;
        private EventHandler<BlockEventArgs>? _eonUpdateHandler;
        private ShutterTxSource? _shutterTxSource = null;

        public override string Name => "ShutterMerge";
        public override string Description => "Shutter Merge plugin for AuRa";
        protected override bool MergeEnabled => ShouldRunSteps(_api);

        public class ShutterLoadingException(string message, Exception? innerException = null) : Exception(message, innerException);

        public override async Task Init(INethermindApi nethermindApi)
        {
            await base.Init(nethermindApi);
            _shutterConfig = _api.Config<IShutterConfig>();
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
            base.InitRpcModulesInternal(blockImprovementContextFactory, false);
            return Task.CompletedTask;
        }


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

                ShutterTxLoader txLoader = new(_api.LogFinder!, _shutterConfig, _api.SpecProvider!, _api.EthereumEcdsa!, readOnlyBlockTree, _api.LogManager);
                _shutterTxSource = new ShutterTxSource(txLoader, _shutterConfig, _api.SpecProvider!, _api.LogManager);

                ShutterMessageHandler shutterMessageHandler = new(_shutterConfig, _shutterTxSource, shutterEon, _api.LogManager);
                _shutterP2P = new(shutterMessageHandler.OnDecryptionKeysReceived, _shutterConfig, _api.LogManager);
                _shutterP2P.Start(_shutterConfig.KeyperP2PAddresses);
            }

            return _api.BlockProducerEnvFactory.Create(_shutterTxSource);
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
            if (validatorsInfo.Count == 0)
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
