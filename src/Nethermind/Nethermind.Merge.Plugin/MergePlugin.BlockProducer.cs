//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using System;
using System.Threading.Tasks;
using Nethermind.Api.Extensions;
using Nethermind.Consensus;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;

namespace Nethermind.Merge.Plugin
{
    public partial class MergePlugin
    {
        private IMiningConfig _miningConfig = null!;
        private PostMergeBlockProducer _postMergeBlockProducer = null!;
        private IManualBlockProductionTrigger? _blockProductionTrigger = null;
        private ManualTimestamper? _manualTimestamper;

        public async Task<IBlockProducer> InitBlockProducer(IConsensusPlugin consensusPlugin)
        {
            if (_mergeConfig.Enabled)
            {
                if (_api.EngineSigner == null) throw new ArgumentNullException(nameof(_api.EngineSigner));
                if (_api.ChainSpec == null) throw new ArgumentNullException(nameof(_api.ChainSpec));
                if (_api.BlockTree == null) throw new ArgumentNullException(nameof(_api.BlockTree));
                if (_api.BlockProcessingQueue == null) throw new ArgumentNullException(nameof(_api.BlockProcessingQueue));
                if (_api.SpecProvider == null) throw new ArgumentNullException(nameof(_api.SpecProvider));
                if (_api.BlockValidator == null) throw new ArgumentNullException(nameof(_api.BlockValidator));
                if (_api.RewardCalculatorSource == null) throw new ArgumentNullException(nameof(_api.RewardCalculatorSource));
                if (_api.ReceiptStorage == null) throw new ArgumentNullException(nameof(_api.ReceiptStorage));
                if (_api.TxPool == null) throw new ArgumentNullException(nameof(_api.TxPool));
                if (_api.DbProvider == null) throw new ArgumentNullException(nameof(_api.DbProvider));
                if (_api.ReadOnlyTrieStore == null) throw new ArgumentNullException(nameof(_api.ReadOnlyTrieStore));
                if (_api.BlockchainProcessor == null) throw new ArgumentNullException(nameof(_api.BlockchainProcessor));
                if (_api.HeaderValidator == null) throw new ArgumentNullException(nameof(_api.HeaderValidator));
                if (_mergeBlockProductionPolicy == null) throw new ArgumentNullException(nameof(_mergeBlockProductionPolicy));
                if (_api.SealValidator == null) throw new ArgumentNullException(nameof(_api.SealValidator));
                
                if (_logger.IsInfo) _logger.Info("Starting Merge block producer & sealer");

                IBlockProducer? blockProducer = _mergeBlockProductionPolicy.ShouldInitPreMergeBlockProduction()
                    ? await consensusPlugin.InitBlockProducer()
                    : null;
                _miningConfig = _api.Config<IMiningConfig>();
                _manualTimestamper ??= new ManualTimestamper();
                _blockProductionTrigger = new BuildBlocksWhenRequested();
                BlockProducerEnv blockProducerEnv = _api.BlockProducerEnvFactory.Create();
                Address feeRecipient;
                if (string.IsNullOrWhiteSpace(_mergeConfig.FeeRecipient))
                {
                    feeRecipient = Address.Zero;
                    if (_logger.IsInfo) _logger.Info("FeeRecipient will be set based on PayloadAttributes.SuggestedFeeRecipient field from CL");
                }
                else
                {
                    feeRecipient = new Address(_mergeConfig.FeeRecipient);
                    if (_logger.IsInfo) _logger.Info($"FeeRecipient: {feeRecipient}");
                }
                
                _api.SealEngine = new MergeSealEngine(_api.SealEngine, _poSSwitcher, feeRecipient, _api.SealValidator, _api.LogManager);
                _api.Sealer = _api.SealEngine;
                PostMergeBlockProducerFactory blockProducerFactory = new(_api.SpecProvider, _api.SealEngine, _manualTimestamper, _miningConfig, _api.LogManager);
                _postMergeBlockProducer = blockProducerFactory.Create(blockProducerEnv, _blockProductionTrigger);
                
                _api.BlockProducer = new MergeBlockProducer(blockProducer, _postMergeBlockProducer, _poSSwitcher);
            }

            return _api.BlockProducer;
        }

        public bool Enabled => _mergeConfig.Enabled;
    }
}
