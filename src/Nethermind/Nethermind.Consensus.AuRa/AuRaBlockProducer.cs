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

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Processing;
using Nethermind.Blockchain.Producers;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Consensus.AuRa.Validators;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Consensus.AuRa
{
    public class AuRaBlockProducer : BlockProducerBase
    {
        private readonly IAuRaStepCalculator _auRaStepCalculator;
        private readonly IReportingValidator _reportingValidator;
        private readonly IAuraConfig _config;

        public AuRaBlockProducer(ITxSource txSource,
            IBlockchainProcessor processor,
            IBlockProductionTrigger blockProductionTrigger,
            IStateProvider stateProvider,
            ISealer sealer,
            IBlockTree blockTree,
            ITimestamper timestamper,
            IAuRaStepCalculator auRaStepCalculator,
            IReportingValidator reportingValidator,
            IAuraConfig config,
            IGasLimitCalculator gasLimitCalculator,
            ISpecProvider specProvider,
            ILogManager logManager) 
            : base(
                txSource,
                processor,
                sealer,
                blockTree,
                blockProductionTrigger,
                stateProvider,
                gasLimitCalculator,
                timestamper,
                specProvider,
                logManager,
                new AuraDifficultyCalculator(auRaStepCalculator))
        {
            _auRaStepCalculator = auRaStepCalculator ?? throw new ArgumentNullException(nameof(auRaStepCalculator));
            _reportingValidator = reportingValidator ?? throw new ArgumentNullException(nameof(reportingValidator));
            _config = config ?? throw new ArgumentNullException(nameof(config));
        }

        protected override Block PrepareBlock(BlockHeader parent)
        {
            var block = base.PrepareBlock(parent);
            block.Header.AuRaStep = _auRaStepCalculator.CurrentStep;
            return block;
        }

        protected override Block? ProcessPreparedBlock(Block block, IBlockTracer? blockTracer)
        {
            Block? processedBlock = base.ProcessPreparedBlock(block, blockTracer);

            if (processedBlock is not null)
            {
                // If force sealing is not on and we didn't pick up any transactions, then we should skip producing block
                if (processedBlock.Transactions.Length == 0)
                {
                    if (_config.ForceSealing)
                    {
                        if (Logger.IsDebug) Logger.Debug($"Force sealing block {block.Number} without transactions.");
                    }
                    else
                    {
                        if (Logger.IsDebug) Logger.Debug($"Skip seal block {block.Number}, no transactions pending.");
                        return null;
                    }
                }
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
