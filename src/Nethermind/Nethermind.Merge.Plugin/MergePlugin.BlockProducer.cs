// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.ApiBase.Extensions;
using Nethermind.Consensus;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Merge.Plugin.BlockProduction;
using Nethermind.Merge.Plugin.Handlers;

namespace Nethermind.Merge.Plugin
{
    public partial class MergePlugin
    {
        protected PostMergeBlockProducer _postMergeBlockProducer = null!;
        protected ManualTimestamper? _manualTimestamper;

        protected virtual PostMergeBlockProducerFactory CreateBlockProducerFactory()
            => new(_api.SpecProvider!, _api.SealEngine, _manualTimestamper!, _blocksConfig, _api.LogManager);

        public virtual IBlockProducer InitBlockProducer(IBlockProducerFactory baseBlockProducerFactory, ITxSource? txSource)
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
                if (_api.BlockchainProcessor is null) throw new ArgumentNullException(nameof(_api.BlockchainProcessor));
                if (_api.HeaderValidator is null) throw new ArgumentNullException(nameof(_api.HeaderValidator));
                if (_mergeBlockProductionPolicy is null) throw new ArgumentNullException(nameof(_mergeBlockProductionPolicy));
                if (_api.SealValidator is null) throw new ArgumentNullException(nameof(_api.SealValidator));
                if (_api.BlockProducerEnvFactory is null) throw new ArgumentNullException(nameof(_api.BlockProducerEnvFactory));

                if (_logger.IsInfo) _logger.Info("Starting Merge block producer & sealer");

                IBlockProducer? blockProducer = _mergeBlockProductionPolicy.ShouldInitPreMergeBlockProduction()
                    ? baseBlockProducerFactory.InitBlockProducer(txSource)
                    : null;
                _manualTimestamper ??= new ManualTimestamper();
                BlockProducerEnv blockProducerEnv = _api.BlockProducerEnvFactory.Create();

                _api.SealEngine = new MergeSealEngine(_api.SealEngine, _poSSwitcher, _api.SealValidator, _api.LogManager);
                _api.Sealer = _api.SealEngine;
                _postMergeBlockProducer = CreateBlockProducerFactory().Create(blockProducerEnv);
                _api.BlockProducer = new MergeBlockProducer(blockProducer, _postMergeBlockProducer, _poSSwitcher);
            }

            return _api.BlockProducer!;
        }

        public IBlockProducerRunner InitBlockProducerRunner(IBlockProducerRunner baseRunner)
        {
            if (MergeEnabled)
            {
                // The trigger can be different, so need to stop the old block production runner at this point.
                StandardBlockProducerRunner postMergeRunner = new StandardBlockProducerRunner(
                    _api.ManualBlockProductionTrigger, _api.BlockTree!, _api.BlockProducer!);
                return new MergeBlockProducerRunner(baseRunner, postMergeRunner, _poSSwitcher);
            }

            return baseRunner;
        }

        // this looks redundant but Enabled actually comes from IConsensusWrapperPlugin
        // while MergeEnabled comes from merge config
        public bool Enabled => MergeEnabled;
    }
}
