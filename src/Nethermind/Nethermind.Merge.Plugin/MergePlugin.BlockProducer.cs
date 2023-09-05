// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Nethermind.Api.Extensions;
using Nethermind.Consensus;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Merge.Plugin.BlockProduction;
using Nethermind.Merge.Plugin.Handlers;

namespace Nethermind.Merge.Plugin
{
    public partial class MergePlugin
    {
        protected PostMergeBlockProducer _postMergeBlockProducer = null!;
        protected IManualBlockProductionTrigger? _blockProductionTrigger = null;
        protected ManualTimestamper? _manualTimestamper;

        protected virtual PostMergeBlockProducerFactory CreateBlockProducerFactory()
            => new(_api.SpecProvider!, _api.SealEngine, _manualTimestamper!, _blocksConfig, _api.LogManager);

        protected virtual BlockProducerEnv CreateBlockProducerEnv()
        {
            Debug.Assert(_api?.BlockProducerEnvFactory is not null,
                $"{nameof(_api.BlockProducerEnvFactory)} has not been initialized.");

            return _api.BlockProducerEnvFactory.Create();
        }

        public virtual async Task<IBlockProducer> InitBlockProducer(IConsensusPlugin consensusPlugin)
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
                if (_api.BlockProducerEnvFactory is null) throw new ArgumentNullException(nameof(_api.BlockProducerEnvFactory));

                if (_logger.IsInfo) _logger.Info("Starting Merge block producer & sealer");

                IBlockProducer? blockProducer = _mergeBlockProductionPolicy.ShouldInitPreMergeBlockProduction()
                    ? await consensusPlugin.InitBlockProducer()
                    : null;
                _manualTimestamper ??= new ManualTimestamper();
                _blockProductionTrigger = new BuildBlocksWhenRequested();
                BlockProducerEnv blockProducerEnv = CreateBlockProducerEnv();

                _api.SealEngine = new MergeSealEngine(_api.SealEngine, _poSSwitcher, _api.SealValidator, _api.LogManager);
                _api.Sealer = _api.SealEngine;
                _postMergeBlockProducer = CreateBlockProducerFactory().Create(
                    blockProducerEnv,
                    _blockProductionTrigger
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
