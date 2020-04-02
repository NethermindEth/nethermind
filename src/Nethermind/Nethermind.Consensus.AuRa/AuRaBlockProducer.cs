//  Copyright (c) 2018 Demerzel Solutions Limited
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

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Producers;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Core;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Consensus.AuRa
{
    public class AuRaBlockProducer : BaseLoopBlockProducer
    {
        private readonly IAuRaStepCalculator _auRaStepCalculator;
        private readonly IAuraConfig _config;
        private readonly Address _nodeAddress;

        public AuRaBlockProducer(IPendingTxSelector pendingTxSelector,
            IBlockchainProcessor processor,
            IStateProvider stateProvider,
            ISealer sealer,
            IBlockTree blockTree,
            IBlockProcessingQueue blockProcessingQueue,
            ITimestamper timestamper,
            ILogManager logManager,
            IAuRaStepCalculator auRaStepCalculator,
            IAuraConfig config,
            Address nodeAddress) 
            : base(pendingTxSelector, processor, sealer, blockTree, blockProcessingQueue, stateProvider, timestamper, logManager, "AuRa")
        {
            _auRaStepCalculator = auRaStepCalculator ?? throw new ArgumentNullException(nameof(auRaStepCalculator));
            _config = config ?? throw new ArgumentNullException(nameof(config));
            CanProduce = _config.AllowAuRaPrivateChains;
            _nodeAddress = nodeAddress ?? throw new ArgumentNullException(nameof(nodeAddress));
        }

        protected override async ValueTask ProducerLoopStep(CancellationToken cancellationToken)
        {
            await base.ProducerLoopStep(cancellationToken);
            var timeToNextStep = _auRaStepCalculator.TimeToNextStep;
            if (Logger.IsDebug) Logger.Debug($"Waiting {timeToNextStep} for next AuRa step.");
            await TaskExt.DelayAtLeast(timeToNextStep, cancellationToken);

        }
        
        protected override Block PrepareBlock(BlockHeader parent)
        {
            var block = base.PrepareBlock(parent);
            block.Header.AuRaStep = _auRaStepCalculator.CurrentStep;
            block.Header.Beneficiary = _nodeAddress;
            return block;
        }

        protected override UInt256 CalculateDifficulty(BlockHeader parent, UInt256 timestamp) 
            => AuraDifficultyCalculator.CalculateDifficulty(parent.AuRaStep.Value, _auRaStepCalculator.CurrentStep);

        protected override bool PreparedBlockCanBeMined(Block block)
        {
            if (base.PreparedBlockCanBeMined(block))
            {
                if (block.Transactions.Length == 0)
                {
                    if (_config.ForceSealing)
                    {
                        if (Logger.IsDebug) Logger.Debug($"Force sealing block {block.Number} without transactions.");
                    }
                    else
                    {
                        if (Logger.IsDebug) Logger.Debug($"Skip seal block {block.Number}, no transactions pending.");
                        return false;
                    }
                }

                return true;

            }
            
            return false;
        }
    }
}