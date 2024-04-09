// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Consensus.AuRa.InitializationSteps;
using Nethermind.Consensus.AuRa.Transactions;
using Nethermind.Core;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Merge.AuRa.Shutter;
using Nethermind.Merge.Plugin;
using Nethermind.Merge.Plugin.BlockProduction;
using Nethermind.Merge.AuRa.Shutter.Contracts;
using Nethermind.Consensus.Processing;

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

            Logging.ILogger logger = _api.LogManager.GetClassLogger();

            ShutterTxSource? shutterTxSource = null;

            if (_auraConfig!.UseShutter)
            {
                // parse validator info file (index, pk)
                IEnumerable<(ulong, byte[])> validatorsInfo = [];
                try
                {
                    JsonDocument validatorsInfoDoc = JsonDocument.Parse(File.ReadAllText(_auraConfig.ShutterValidatorInfoFile));
                    validatorsInfo = validatorsInfoDoc.RootElement.EnumerateObject().Select((JsonProperty p) => (Convert.ToUInt64(p.Name), Convert.FromHexString(p.Value.GetString()!.Substring(2))));
                }
                catch (Exception e)
                {
                    throw new Exception("Could not load Shutter validator info file: " + e.Message);
                }

                IReadOnlyBlockTree readOnlyBlockTree = _api.BlockTree!.AsReadOnly();
                ReadOnlyTxProcessingEnvFactory readOnlyTxProcessingEnvFactory = new(_api.WorldStateManager!, readOnlyBlockTree, _api.SpecProvider, _api.LogManager);

                // init Shutter transaction source
                shutterTxSource = new ShutterTxSource(_api.LogFinder!, _api.FilterStore!, readOnlyTxProcessingEnvFactory, _api.AbiEncoder, _auraConfig, _api.SpecProvider!, _api.LogManager, _api.EthereumEcdsa!, validatorsInfo);

                // init P2P to listen for decryption keys
                Action<Shutter.Dto.DecryptionKeys> onDecryptionKeysReceived = (Shutter.Dto.DecryptionKeys decryptionKeys) =>
                {
                    if (shutterTxSource.DecryptionKeys is null || decryptionKeys.Gnosis.Slot > shutterTxSource.DecryptionKeys!.Gnosis.Slot)
                    {
                        if (logger.IsInfo) logger.Info("Updated Shutter decryption keys...");
                        shutterTxSource.DecryptionKeys = decryptionKeys;

                        if (shutterTxSource.TxPointer is null)
                        {
                            shutterTxSource.TxPointer = decryptionKeys.Gnosis.TxPointer;
                        }
                    }
                };

                ShutterP2P shutterP2P = new(onDecryptionKeysReceived, readOnlyBlockTree, readOnlyTxProcessingEnvFactory, _api.AbiEncoder!, _auraConfig, _api.LogManager);
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
