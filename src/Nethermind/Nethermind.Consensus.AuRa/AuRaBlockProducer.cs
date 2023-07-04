// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Consensus.AuRa.Validators;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
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
            IWorldState stateProvider,
            ISealer sealer,
            IBlockTree blockTree,
            ITimestamper timestamper,
            IAuRaStepCalculator auRaStepCalculator,
            IReportingValidator reportingValidator,
            IAuraConfig config,
            IGasLimitCalculator gasLimitCalculator,
            ISpecProvider specProvider,
            ILogManager logManager,
            IBlocksConfig blocksConfig)
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
                new AuraDifficultyCalculator(auRaStepCalculator),
                blocksConfig)
        {
            _auRaStepCalculator = auRaStepCalculator ?? throw new ArgumentNullException(nameof(auRaStepCalculator));
            _reportingValidator = reportingValidator ?? throw new ArgumentNullException(nameof(reportingValidator));
            _config = config ?? throw new ArgumentNullException(nameof(config));
        }

        protected override Block PrepareBlock(BlockHeader parent, PayloadAttributes? payloadAttributes = null)
        {
            Block block = base.PrepareBlock(parent, payloadAttributes);
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
