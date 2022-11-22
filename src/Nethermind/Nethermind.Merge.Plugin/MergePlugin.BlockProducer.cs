// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Api.Extensions;
using Nethermind.Consensus;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.BlockProduction;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.State;

namespace Nethermind.Merge.Plugin
{
    public partial class MergePlugin
    {
        private IMiningConfig _miningConfig = null!;
        private PostMergeBlockProducer _postMergeBlockProducer = null!;
        private IManualBlockProductionTrigger? _blockProductionTrigger = null;
        private ManualTimestamper? _manualTimestamper;

        protected virtual ITxSource? CreateTxSource(IStateProvider stateProvider) => null;

        public async Task<IBlockProducer> InitBlockProducer(IConsensusPlugin consensusPlugin)
        {
            if (MergeEnabled)
            {
                if (_api.EngineSigner is null) throw new ArgumentNullException(nameof(_api.EngineSigner));
                if (_api.ChainSpec is null) throw new ArgumentNullException(nameof(_api.ChainSpec));
                if (_api.BlockTree is null) throw new ArgumentNullException(nameof(_api.BlockTree));
                if (_api.BlockProcessingQueue is null) throw new ArgumentNullException(nameof(_api.BlockProcessingQueue));
                if (_api.SpecProvider is null) throw new ArgumentNullException(nameof(_api.SpecProvider));
                if (_api.BlockValidator is null) throw new ArgumentNullException(nameof(_api.BlockValidator));
                if (_api.RewardCalculatorSource is null) throw new ArgumentNullException(nameof(_api.RewardCalculatorSource));
                if (_api.ReceiptStorage is null) throw new ArgumentNullException(nameof(_api.ReceiptStorage));
                if (_api.TxPool is null) throw new ArgumentNullException(nameof(_api.TxPool));
                if (_api.DbProvider is null) throw new ArgumentNullException(nameof(_api.DbProvider));
                if (_api.ReadOnlyTrieStore is null) throw new ArgumentNullException(nameof(_api.ReadOnlyTrieStore));
                if (_api.BlockchainProcessor is null) throw new ArgumentNullException(nameof(_api.BlockchainProcessor));
                if (_api.HeaderValidator is null) throw new ArgumentNullException(nameof(_api.HeaderValidator));
                if (_mergeBlockProductionPolicy is null) throw new ArgumentNullException(nameof(_mergeBlockProductionPolicy));
                if (_api.SealValidator is null) throw new ArgumentNullException(nameof(_api.SealValidator));

                if (_logger.IsInfo) _logger.Info("Starting Merge block producer & sealer");

                IBlockProducer? blockProducer = _mergeBlockProductionPolicy.ShouldInitPreMergeBlockProduction()
                    ? await consensusPlugin.InitBlockProducer()
                    : null;
                _miningConfig = _api.Config<IMiningConfig>();
                _manualTimestamper ??= new ManualTimestamper();
                _blockProductionTrigger = new BuildBlocksWhenRequested();
                BlockProducerEnv blockProducerEnv = _api.BlockProducerEnvFactory.Create();

                _api.SealEngine = new MergeSealEngine(_api.SealEngine, _poSSwitcher, _api.SealValidator, _api.LogManager);
                _api.Sealer = _api.SealEngine;
                PostMergeBlockProducerFactory blockProducerFactory = new(_api.SpecProvider, _api.SealEngine, _manualTimestamper, _miningConfig, _api.LogManager);
                _postMergeBlockProducer = blockProducerFactory.Create(
                    blockProducerEnv,
                    _blockProductionTrigger,
                    CreateTxSource(blockProducerEnv.ReadOnlyStateProvider)
                );

                _api.BlockProducer = new MergeBlockProducer(blockProducer, _postMergeBlockProducer, _poSSwitcher);
            }

            return _api.BlockProducer!;
        }

        // this looks redundant but Enabled actually comes from IConsensusWrapperPlugin
        // while MergeEnabled comes from merge config
        public bool Enabled => MergeEnabled;
    }
}
