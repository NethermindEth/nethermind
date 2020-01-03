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
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.AuRa.Config;
using Nethermind.Blockchain;
using Nethermind.Blockchain.TxPools;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.Mining;
using Nethermind.Store;

namespace Nethermind.AuRa
{
    public class AuRaBlockProducer : BaseLoopBlockProducer
    {
        private readonly IAuRaStepCalculator _auRaStepCalculator;
        private readonly IAuraConfig _config;
        private readonly Address _nodeAddress;

        public AuRaBlockProducer(IPendingTransactionSelector pendingTransactionSelector,
            IBlockchainProcessor processor,
            ISealer sealer,
            IBlockTree blockTree,
            IStateProvider stateProvider,
            ITimestamper timestamper,
            ILogManager logManager,
            IAuRaStepCalculator auRaStepCalculator,
            IAuraConfig config,
            Address nodeAddress) 
            : base(pendingTransactionSelector, processor, sealer, blockTree, stateProvider, timestamper, logManager, "AuRa")
        {
            _auRaStepCalculator = auRaStepCalculator ?? throw new ArgumentNullException(nameof(auRaStepCalculator));
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _nodeAddress = nodeAddress ?? throw new ArgumentNullException(nameof(nodeAddress));
        }

        protected override async ValueTask BetweenBlocks()
        {
            var timeToNextStep = _auRaStepCalculator.TimeToNextStep;
            if (Logger.IsDebug) Logger.Debug($"Waiting {timeToNextStep} for next AuRa step.");
            await TaskExt.DelayAtLeast(timeToNextStep);
        }

        protected override Block PrepareBlock(BlockHeader parent)
        {
            var block = base.PrepareBlock(parent);
            block.Header.AuRaStep = _auRaStepCalculator.CurrentStep;
            block.Beneficiary = _nodeAddress;
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