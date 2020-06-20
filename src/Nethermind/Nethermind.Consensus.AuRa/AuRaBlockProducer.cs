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
using Nethermind.Blockchain.Processing;
using Nethermind.Blockchain.Producers;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Consensus.AuRa.Validators;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Consensus.AuRa
{
    public class AuRaBlockProducer : BaseLoopBlockProducer
    {
        private readonly IAuRaStepCalculator _auRaStepCalculator;
        private readonly IReportingValidator _reportingValidator;
        private readonly IAuraConfig _config;
        private readonly IGasLimitOverride _gasLimitOverride;

        public AuRaBlockProducer(
            ITxSource txSource,
            IBlockchainProcessor processor,
            IStateProvider stateProvider,
            ISealer sealer,
            IBlockTree blockTree,
            IBlockProcessingQueue blockProcessingQueue,
            ITimestamper timestamper,
            ILogManager logManager,
            IAuRaStepCalculator auRaStepCalculator,
            IReportingValidator reportingValidator,
            IAuraConfig config,
            IGasLimitOverride gasLimitOverride = null) 
            : base(txSource, processor, sealer, blockTree, blockProcessingQueue, stateProvider, timestamper, logManager, "AuRa")
        {
            _auRaStepCalculator = auRaStepCalculator ?? throw new ArgumentNullException(nameof(auRaStepCalculator));
            _reportingValidator = reportingValidator ?? throw new ArgumentNullException(nameof(reportingValidator));
            _config = config ?? throw new ArgumentNullException(nameof(config));
            CanProduce = _config.AllowAuRaPrivateChains;
            _gasLimitOverride = gasLimitOverride;
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
            return block;
        }

        protected override long GetGasLimit(BlockHeader parent) => _gasLimitOverride?.GetGasLimit(parent) ?? base.GetGasLimit(parent);

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

        protected override Block ProcessPreparedBlock(Block block)
        {
            var processedBlock = base.ProcessPreparedBlock(block);
            
            // We need to check if we are within gas limit. We cannot calculate this in advance because:
            // a) GasLimit can come from contract
            // b) Some transactions that call contracts can be added to block and we don't know how much gas they will use.
            if (processedBlock.GasUsed > processedBlock.GasLimit)
            {
                if (Logger.IsError) Logger.Error($"Block produced used {processedBlock.GasUsed} gas and exceeded gas limit {processedBlock.GasLimit}.");
                return null;
            }
            
            return processedBlock;
        }

        protected override Task<Block> SealBlock(Block block, BlockHeader parent, CancellationToken token)
        {
            // if (block.Number < EmptyStepsTransition)
            _reportingValidator.TryReportSkipped(block.Header, parent);
            return base.SealBlock(block, parent, token);
        }
    }
}
