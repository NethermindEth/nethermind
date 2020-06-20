﻿//  Copyright (c) 2018 Demerzel Solutions Limited
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

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Processing;
using Nethermind.Consensus;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Blockchain.Producers
{
    /// <summary>
    /// This is developer only producer. Differs from <see cref="DevBlockProducer"/> that it uses background thread for block production instead of just listening on txPool. 
    /// </summary>
    public class DevBackgroundBlockProducer : BaseLoopBlockProducer
    {
        private const int DelayBetweenBlocks = 5_000;

        public DevBackgroundBlockProducer(
            ITxSource txSource,
            IBlockchainProcessor processor,
            IBlockTree blockTree,
            IBlockProcessingQueue blockProcessingQueue,
            IStateProvider stateProvider,
            ITimestamper timestamper,
            ILogManager logManager) : base(txSource, processor, NullSealEngine.Instance, blockTree, blockProcessingQueue, stateProvider, timestamper, logManager, "Dev")
        {
        }

        protected override UInt256 CalculateDifficulty(BlockHeader parent, UInt256 timestamp) => 1;

        protected override async ValueTask ProducerLoopStep(CancellationToken cancellationToken)
        {
            await base.ProducerLoopStep(cancellationToken);
            await Task.Delay(DelayBetweenBlocks, cancellationToken);
        }
    }
}
