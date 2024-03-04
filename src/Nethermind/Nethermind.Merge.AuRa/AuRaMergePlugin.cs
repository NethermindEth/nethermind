// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Consensus.AuRa.Contracts;
using Nethermind.Consensus.AuRa.InitializationSteps;
using Nethermind.Consensus.AuRa.Transactions;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Evm.Tracing.GethStyle.JavaScript;
using Nethermind.Merge.AuRa.Shutter;
using Nethermind.Merge.Plugin;
using Nethermind.Merge.Plugin.BlockProduction;
using Nethermind.TxPool;

namespace Nethermind.Merge.AuRa
{
    /// <summary>
    /// Plugin for AuRa -> PoS migration
    /// </summary>
    /// <remarks>IMPORTANT: this plugin should always come before MergePlugin</remarks>
    public class AuRaMergePlugin : MergePlugin, IInitializationPlugin
    {
        private AuRaNethermindApi? _auraApi;
        private IAuraConfig? _auraConfig;

        public override string Name => "AuRaMerge";
        public override string Description => "AuRa Merge plugin for ETH1-ETH2";
        protected override bool MergeEnabled => ShouldRunSteps(_api);

        public override async Task Init(INethermindApi nethermindApi)
        {
            _api = nethermindApi;
            _auraConfig = _api.Config<IAuraConfig>();
            _mergeConfig = _api.Config<IMergeConfig>();

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

        public override Task<IBlockProducer> InitBlockProducer(IConsensusPlugin consensusPlugin)
        {
            _api.BlockProducerEnvFactory = new AuRaMergeBlockProducerEnvFactory(
                _auraApi!,
                _auraConfig!,
                _api.DisposeStack,
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

            return base.InitBlockProducer(consensusPlugin);
        }

        protected override PostMergeBlockProducerFactory CreateBlockProducerFactory()
            => new AuRaPostMergeBlockProducerFactory(
                _api.SpecProvider!,
                _api.SealEngine,
                _manualTimestamper!,
                _blocksConfig,
                _api.LogManager);

        protected override BlockProducerEnv CreateBlockProducerEnv()
        {
            Debug.Assert(_api?.BlockProducerEnvFactory is not null,
                $"{nameof(_api.BlockProducerEnvFactory)} has not been initialized.");

            ShutterTxSource? shutterTxSource = null;

            if (_auraConfig!.UseShutter)
            {
                Address validatorContractAddress = _api.ChainSpec.AuRa.Validators.GetContractAddress();
                ReadOnlyTxProcessingEnv readonlyTxProcessorSource = new(_api.WorldStateManager!, _api.BlockTree, _api.SpecProvider, _api.LogManager);
                ValidatorContract validatorContract = new(_api.TransactionProcessor!, _api.AbiEncoder, validatorContractAddress, _api.WorldState!, readonlyTxProcessorSource, _api.EngineSigner!);
                BlockHeader blockHeader = _api.BlockTree!.Head!.Header;
                Shutter.Contracts.ValidatorRegistryContract validatorRegistryContract = new(_api.TransactionProcessor!, _api.AbiEncoder, _auraConfig!.ShutterValidatorRegistryContractAddress.ToAddress(), _api.EngineSigner!, _api.TxSender!, new TxSealer(_api.EngineSigner!, _api.Timestamper!), validatorContract, blockHeader);
                if (!validatorRegistryContract.IsRegistered(blockHeader))
                {
                    // todo: safe to do this in another thread?
                    var _ = validatorRegistryContract.Register(blockHeader);
                }

                LogFinder logFinder = new(_api.BlockTree, _api.ReceiptFinder, _api.ReceiptStorage, _api.BloomStorage, _api.LogManager, new ReceiptsRecovery(_api.EthereumEcdsa, _api.SpecProvider));
                shutterTxSource = new ShutterTxSource(_auraConfig.ShutterSequencerContractAddress, logFinder, _api.FilterStore!);

                Action<Shutter.Dto.DecryptionKeys> onDecryptionKeysReceived = (Shutter.Dto.DecryptionKeys decryptionKeys) =>
                {
                    if (decryptionKeys.Gnosis.Slot > shutterTxSource.DecryptionKeys.Gnosis.Slot)
                    {
                        shutterTxSource.DecryptionKeys = decryptionKeys;
                    }
                };
                Shutter.Contracts.KeyBroadcastContract keyBroadcastContract = new(_api.TransactionProcessor!, _api.AbiEncoder, new(_auraConfig!.ShutterKeyBroadcastContractAddress));
                Shutter.Contracts.KeyperSetManagerContract keyperSetManagerContract = new(_api.TransactionProcessor!, _api.AbiEncoder, new(_auraConfig!.ShutterKeyperSetManagerContractAddress));
                ShutterP2P shutterP2P = new(onDecryptionKeysReceived, keyBroadcastContract, keyperSetManagerContract, _api, _auraConfig.ShutterKeyperP2PAddresses, _auraConfig.ShutterP2PPort.ToString());
            }

            return _api.BlockProducerEnvFactory.Create(shutterTxSource);
        }

        public bool ShouldRunSteps(INethermindApi api)
        {
            _mergeConfig = api.Config<IMergeConfig>();
            return _mergeConfig.Enabled && api.ChainSpec.SealEngineType == SealEngineType.AuRa;
        }
    }
}
