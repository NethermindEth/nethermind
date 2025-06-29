// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Consensus;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Merge.Plugin.BlockProduction;

namespace Nethermind.Merge.Plugin
{
    public partial class MergePlugin
    {
        protected ManualTimestamper? _manualTimestamper;

        protected virtual PostMergeBlockProducerFactory CreateBlockProducerFactory()
            => new(_api.SpecProvider!, _api.SealEngine, _manualTimestamper!, _blocksConfig, _api.LogManager);

        public virtual IBlockProducer InitBlockProducer(IBlockProducerFactory baseBlockProducerFactory)
        {
            if (MergeEnabled)
            {
                ArgumentNullException.ThrowIfNull(_api.EngineSigner);
                ArgumentNullException.ThrowIfNull(_api.ChainSpec);
                ArgumentNullException.ThrowIfNull(_api.BlockTree);
                ArgumentNullException.ThrowIfNull(_api.BlockProcessingQueue);
                ArgumentNullException.ThrowIfNull(_api.SpecProvider);
                ArgumentNullException.ThrowIfNull(_api.BlockValidator);
                ArgumentNullException.ThrowIfNull(_api.RewardCalculatorSource);
                ArgumentNullException.ThrowIfNull(_api.ReceiptStorage);
                ArgumentNullException.ThrowIfNull(_api.TxPool);
                ArgumentNullException.ThrowIfNull(_api.DbProvider);
                ArgumentNullException.ThrowIfNull(_api.HeaderValidator);
                ArgumentNullException.ThrowIfNull(_mergeBlockProductionPolicy);
                ArgumentNullException.ThrowIfNull(_api.SealValidator);
                ArgumentNullException.ThrowIfNull(_api.BlockProducerEnvFactory);

                if (_logger.IsInfo) _logger.Info("Starting Merge block producer & sealer");

                IBlockProducer? blockProducer = _mergeBlockProductionPolicy.ShouldInitPreMergeBlockProduction()
                    ? baseBlockProducerFactory.InitBlockProducer()
                    : null;
                _manualTimestamper ??= new ManualTimestamper();

                IBlockProducerEnv blockProducerEnv = _api.BlockProducerEnvFactory.Create();

                PostMergeBlockProducer postMergeBlockProducer = CreateBlockProducerFactory().Create(blockProducerEnv);
                _api.BlockProducer = new MergeBlockProducer(blockProducer, postMergeBlockProducer, _poSSwitcher);
            }

            return _api.BlockProducer!;
        }

        public IBlockProducerRunner InitBlockProducerRunner(IBlockProducerRunnerFactory baseRunnerFactory, IBlockProducer blockProducer)
        {
            if (MergeEnabled)
            {
                IMergeBlockProducer mergeBlockProducer = blockProducer as IMergeBlockProducer
                    ?? throw new ArgumentException("Merge enabled, but block producer is not IMergeBlockProducer");

                IBlockProducer? preMergeBlockProducer = mergeBlockProducer.PreMergeBlockProducer;
                IBlockProducerRunner? preMergeRunner = preMergeBlockProducer is not null
                    ? baseRunnerFactory.InitBlockProducerRunner(preMergeBlockProducer)
                    : null;

                StandardBlockProducerRunner postMergeRunner = new StandardBlockProducerRunner(
                    _api.ManualBlockProductionTrigger, _api.BlockTree!, mergeBlockProducer);

                return new MergeBlockProducerRunner(preMergeRunner, postMergeRunner, _poSSwitcher);
            }

            return baseRunnerFactory.InitBlockProducerRunner(blockProducer);
        }

        // this looks redundant but Enabled actually comes from IConsensusWrapperPlugin
        // while MergeEnabled comes from merge config
        public bool Enabled => MergeEnabled;
    }
}
