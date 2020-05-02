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

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Processing;
using Nethermind.Blockchain.Producers;
using Nethermind.Consensus;
using Nethermind.Core.Extensions;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Core.Test.Blockchain
{
    public class TestBlockProducer : BaseLoopBlockProducer
    {
        public TestBlockProducer(ITxSource transactionSource, IBlockchainProcessor processor, IStateProvider stateProvider, ISealer sealer, IBlockTree blockTree, IBlockProcessingQueue blockProcessingQueue, ITimestamper timestamper, ILogManager logManager)
            : base(transactionSource, processor, sealer, blockTree, blockProcessingQueue, stateProvider, timestamper, logManager, "a")
        {
        }

        private AutoResetEvent _newBlockArrived = new AutoResetEvent(false);

        public void BuildNewBlock()
        {
            _newBlockArrived.Set();
        }

        protected override async ValueTask ProducerLoop()
        {
            while (true)
            {
                await _newBlockArrived.WaitOneAsync(LoopCancellationTokenSource.Token);
                await TryProduceNewBlock(LoopCancellationTokenSource.Token);
            }
            
            // ReSharper disable once FunctionNeverReturns
        }

        protected override UInt256 CalculateDifficulty(BlockHeader parent, UInt256 timestamp)
        {
            return 1;
        }
    }
}